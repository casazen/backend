using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Guests of a stay (CO-12, A5-02). Validation follows the Alloggiati record (<see cref="AlloggiatiRecordRules"/>):
/// kinds and order, sex male/female only, comune and province only when born in Italy, the document (kind, number,
/// place of issue) only for a single guest or a head of family or group. A code, when given, must have the shape of the
/// record field and, once the official table is imported, exist in it; without codes the names are stored and the codes
/// stay "to complete" (they block only the export of the record).
/// </summary>
public class StayGuestService(
    AppDbContext db,
    IAlloggiatiCodeTableService codeTables,
    TimeProvider? timeProvider = null) : IStayGuestService
{
    /// <summary>Longest name stored for places, citizenship and document labels.</summary>
    public const int MaxLabelLength = 100;

    /// <summary>Length of <see cref="StayGuest.EnteredByUserId"/>.</summary>
    private const int MaxUserIdLength = 200;

    /// <summary>The earliest issue day of a document that is accepted (NF-40).</summary>
    private static readonly DateOnly EarliestDocumentDate = new(1900, 1, 1);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<StayGuest>> GetForBookingAsync(Booking booking, CancellationToken cancellationToken = default)
    {
        var stored = await db.StayGuests
            .AsNoTracking()
            .Where(s => s.BookingId == booking.Id)
            .OrderBy(s => s.Position)
            .ToListAsync(cancellationToken);
        if (stored.Count > 0)
            return stored;

        var booker = booking.Guest
            ?? await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == booking.GuestId, cancellationToken);
        return booker is null ? [] : [StayGuest.FromBooker(booking, booker)];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StayGuest>>> GetForBookingsAsync(
        IReadOnlyCollection<Booking> bookings,
        CancellationToken cancellationToken = default)
    {
        var ids = bookings.Select(b => b.Id).ToList();
        var stored = (await db.StayGuests
                .AsNoTracking()
                .Where(s => ids.Contains(s.BookingId))
                .ToListAsync(cancellationToken))
            .ToLookup(s => s.BookingId);

        var result = new Dictionary<Guid, IReadOnlyList<StayGuest>>();
        foreach (var booking in bookings)
        {
            var rows = stored[booking.Id].OrderBy(s => s.Position).ToList();
            if (rows.Count == 0 && booking.Guest is { } booker)
                rows.Add(StayGuest.FromBooker(booking, booker));
            result[booking.Id] = rows;
        }

        return result;
    }

    public async Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(
        IReadOnlyList<StayGuestInput> guests,
        CancellationToken cancellationToken = default)
    {
        var (_, errors) = await BuildAsync(guests, readTaxExemption: false, cancellationToken);
        return errors;
    }

    public async Task<StayGuestSaveResult> ReplaceAsync(
        Booking booking,
        IReadOnlyList<StayGuestInput> guests,
        StayGuestAuthor author,
        bool save = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(author);

        // The tourist tax exemption is the host's decision (NF-46): any other author keeps the one of each row and is never read for it.
        var hostAuthor = author.Source == StayGuestDataSource.Host;
        var (candidates, errors) = await BuildAsync(guests, hostAuthor, cancellationToken);
        if (errors.Count > 0)
            return new StayGuestSaveResult([], errors);

        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await db.StayGuests
            .Where(s => s.BookingId == booking.Id)
            .OrderBy(s => s.Position)
            .ToListAsync(cancellationToken);

        var saved = new List<StayGuest>(candidates.Count);
        for (var position = 0; position < candidates.Count; position++)
        {
            var row = existing.FirstOrDefault(s => s.Position == position);
            if (row is null)
            {
                row = new StayGuest
                {
                    BookingId = booking.Id,
                    OrgId = booking.OrgId, // the guests belong to their booking's org (TN-2)
                    Position = position,
                    CreatedAt = now,
                };
                db.StayGuests.Add(row);
            }

            CopyData(candidates[position], row, copyTaxExemption: hostAuthor);
            // The first line stays linked to the booker, whose contact data and consent live on Guest.
            row.GuestId = position == 0 ? booking.GuestId : null;
            row.DataSource = author.Source;
            row.EnteredByUserId = author.UserId is { Length: > MaxUserIdLength } userId ? userId[..MaxUserIdLength] : author.UserId;
            row.UpdatedAt = now;
            saved.Add(row);
        }

        db.StayGuests.RemoveRange(existing.Where(s => s.Position >= candidates.Count));

        if (save)
            await db.SaveChangesAsync(cancellationToken);

        return new StayGuestSaveResult(saved, []);
    }

    /// <summary>
    /// Normalized rows from the input, and the field errors that stop them from being saved. The tourist tax exemption is read and checked
    /// only when <paramref name="readTaxExemption"/> (a host author); otherwise every candidate has none.
    /// </summary>
    private async Task<(List<StayGuest> Candidates, List<StayGuestFieldError> Errors)> BuildAsync(
        IReadOnlyList<StayGuestInput> guests,
        bool readTaxExemption,
        CancellationToken cancellationToken)
    {
        var errors = new List<StayGuestFieldError>();
        if (guests.Count is 0 or > AlloggiatiRecordRules.MaxStayGuests)
        {
            errors.Add(new StayGuestFieldError(null, "Guests", CheckInValidationKeys.GuestsCount, AlloggiatiRecordRules.MaxStayGuests));
            return ([], errors);
        }

        var candidates = new List<StayGuest>(guests.Count);
        var types = new List<StayGuestType>(guests.Count);
        for (var i = 0; i < guests.Count; i++)
        {
            var input = guests[i];
            StayGuestType type = default;
            if (string.IsNullOrWhiteSpace(input.Type))
                errors.Add(Error(i, nameof(StayGuestInput.Type), CheckInValidationKeys.FieldRequired));
            else if (!Enum.TryParse(input.Type.Trim(), ignoreCase: true, out type) || !Enum.IsDefined(type) || int.TryParse(input.Type, out _))
                errors.Add(Error(i, nameof(StayGuestInput.Type), CheckInValidationKeys.GuestTypeInvalid));
            else
                types.Add(type);

            var candidate = Normalize(input, type);
            candidate.TaxExemptionCode = readTaxExemption ? NormalizeExemptionCode(input.TaxExemptionCode) : null;
            candidates.Add(candidate);
        }

        if (types.Count == guests.Count)
        {
            foreach (var error in AlloggiatiRecordRules.CompositionErrors(types))
            {
                var key = error.Kind switch
                {
                    StayGuestCompositionErrorKind.MemberWithoutHead => CheckInValidationKeys.MemberWithoutHead,
                    StayGuestCompositionErrorKind.HeadWithoutMembers => CheckInValidationKeys.HeadWithoutMembers,
                    _ => CheckInValidationKeys.GuestTypeInvalid,
                };
                errors.Add(Error(error.Index, nameof(StayGuestInput.Type), key));
            }
        }

        var codeBook = await codeTables.LoadCodeBookAsync(candidates, cancellationToken);
        var today = _clock.TodayInRome();
        for (var i = 0; i < guests.Count; i++)
            ValidateGuest(i, guests[i], candidates[i], codeBook, today, errors);

        if (readTaxExemption)
            await ValidateTaxExemptionsAsync(candidates, errors, cancellationToken);

        return (candidates, errors);
    }

    /// <summary>The exemption of a guest is an active reason the host may choose: never the automatic one (the age) nor an unknown code.</summary>
    private async Task ValidateTaxExemptionsAsync(
        List<StayGuest> candidates,
        List<StayGuestFieldError> errors,
        CancellationToken cancellationToken)
    {
        if (candidates.All(c => c.TaxExemptionCode is null))
            return;

        var choosable = (await db.TouristTaxExemptionReasons
                .AsNoTracking()
                .Where(r => r.IsActive && !r.IsAutomatic)
                .Select(r => r.Code)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].TaxExemptionCode is { } code && !choosable.Contains(code))
                errors.Add(Error(i, nameof(StayGuestInput.TaxExemptionCode), CheckInValidationKeys.TaxExemptionInvalid));
        }
    }

    /// <summary>An exemption reason code as stored: trimmed and lower case, null when empty (an unknown code is refused by the check).</summary>
    private static string? NormalizeExemptionCode(string? code)
    {
        var value = code?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static StayGuest Normalize(StayGuestInput input, StayGuestType type)
    {
        var row = new StayGuest
        {
            Type = type,
            FirstName = Clean(input.FirstName),
            LastName = Clean(input.LastName),
            Gender = input.Gender,
            DateOfBirth = input.DateOfBirth?.Date,
            BornInItaly = input.BornInItaly,
            CitizenshipCode = AlloggiatiRecordRules.NormalizeCode(input.CitizenshipCode),
            CitizenshipName = Clean(input.CitizenshipName),
            ResidenceInItaly = input.ResidenceInItaly,
            TourismType = IstatGuestFieldLists.Normalize(input.TourismType),
            TravelWay = IstatGuestFieldLists.Normalize(input.TravelWay),
            EducationLevel = IstatGuestFieldLists.Normalize(input.EducationLevel),
        };

        // The residence follows the shape of the birth place: comune and province in Italy, a state abroad, nothing when not said.
        if (input.ResidenceInItaly == true)
        {
            row.ResidenceComuneCode = AlloggiatiRecordRules.NormalizeCode(input.ResidenceComuneCode);
            row.ResidenceComuneName = NullIfEmpty(Clean(input.ResidenceComuneName));
            row.ResidenceProvince = AlloggiatiRecordRules.NormalizeCode(input.ResidenceProvince);
        }
        else if (input.ResidenceInItaly == false)
        {
            row.ResidenceCountryCode = AlloggiatiRecordRules.NormalizeCode(input.ResidenceCountryCode);
            row.ResidenceCountryName = NullIfEmpty(Clean(input.ResidenceCountryName));
        }

        if (input.BornInItaly == true)
        {
            row.BirthComuneCode = AlloggiatiRecordRules.NormalizeCode(input.BirthComuneCode);
            row.BirthComuneName = Clean(input.BirthComuneName);
            row.BirthProvince = AlloggiatiRecordRules.NormalizeCode(input.BirthProvince);
        }
        else if (input.BornInItaly == false)
        {
            row.BirthCountryCode = AlloggiatiRecordRules.NormalizeCode(input.BirthCountryCode);
            row.BirthCountryName = Clean(input.BirthCountryName);
        }

        // Family and group members have no document in the record: whatever was sent is dropped.
        if (AlloggiatiRecordRules.RequiresDocument(type))
        {
            row.DocumentType = Enum.TryParse<GuestDocumentType>(input.DocumentType?.Trim(), ignoreCase: true, out var kind)
                && kind is GuestDocumentType.Passport or GuestDocumentType.IdentityCard or GuestDocumentType.DriversLicense
                    ? kind
                    : null;
            row.DocumentTypeCode = AlloggiatiRecordRules.NormalizeCode(input.DocumentTypeCode);
            row.DocumentNumber = AlloggiatiRecordRules.NormalizeDocumentNumber(input.DocumentNumber);
            row.DocumentIssuePlaceCode = AlloggiatiRecordRules.NormalizeCode(input.DocumentIssuePlaceCode);
            row.DocumentIssuePlaceName = Clean(input.DocumentIssuePlaceName);
            row.DocumentIssuedOn = input.DocumentIssuedOn;
            row.DocumentExpiresOn = input.DocumentExpiresOn;
        }

        return row;
    }

    private void ValidateGuest(
        int index,
        StayGuestInput input,
        StayGuest row,
        AlloggiatiCodeBook codeBook,
        DateTime today,
        List<StayGuestFieldError> errors)
    {
        RequireText(index, nameof(StayGuestInput.FirstName), row.FirstName, AlloggiatiRecordRules.MaxFirstNameLength, errors);
        RequireText(index, nameof(StayGuestInput.LastName), row.LastName, AlloggiatiRecordRules.MaxLastNameLength, errors);

        if (input.Gender is null)
            errors.Add(Error(index, nameof(StayGuestInput.Gender), CheckInValidationKeys.FieldRequired));
        else if (input.Gender is not (Gender.Male or Gender.Female))
            // Alloggiati Web accepts only 1 = male and 2 = female (tracciato record, field "Sesso").
            errors.Add(Error(index, nameof(StayGuestInput.Gender), CheckInValidationKeys.GenderInvalid));

        if (row.DateOfBirth is not { } birth)
            errors.Add(Error(index, nameof(StayGuestInput.DateOfBirth), CheckInValidationKeys.FieldRequired));
        else if (birth > today)
            errors.Add(Error(index, nameof(StayGuestInput.DateOfBirth), CheckInValidationKeys.DateOfBirthInFuture));

        switch (row.BornInItaly)
        {
            case null:
                errors.Add(Error(index, nameof(StayGuestInput.BornInItaly), CheckInValidationKeys.FieldRequired));
                break;
            case true:
                ValidatePlace(index, nameof(StayGuestInput.BirthComuneName), nameof(StayGuestInput.BirthComuneCode),
                    row.BirthComuneName, row.BirthComuneCode, [AlloggiatiCodeTable.Comuni], codeBook, errors,
                    entry =>
                    {
                        row.BirthComuneName = entry.Description;
                        if (entry.Province is not null)
                            row.BirthProvince = entry.Province;
                    });
                if (string.IsNullOrEmpty(row.BirthProvince))
                    errors.Add(Error(index, nameof(StayGuestInput.BirthProvince), CheckInValidationKeys.FieldRequired));
                else if (!AlloggiatiRecordRules.IsValidProvince(row.BirthProvince))
                    errors.Add(Error(index, nameof(StayGuestInput.BirthProvince), CheckInValidationKeys.ProvinceInvalid));
                break;
            case false:
                ValidatePlace(index, nameof(StayGuestInput.BirthCountryName), nameof(StayGuestInput.BirthCountryCode),
                    row.BirthCountryName, row.BirthCountryCode, [AlloggiatiCodeTable.Stati], codeBook, errors,
                    entry => row.BirthCountryName = entry.Description);
                break;
        }

        ValidatePlace(index, nameof(StayGuestInput.CitizenshipName), nameof(StayGuestInput.CitizenshipCode),
            row.CitizenshipName, row.CitizenshipCode, [AlloggiatiCodeTable.Stati], codeBook, errors,
            entry => row.CitizenshipName = entry.Description);

        // NF-40: the residence and the statistical fields are optional and checked for their shape only; whether one is required depends on
        // the sender and the region, and is decided by the readiness rules of the check-in, never here.
        ValidateResidence(index, row, codeBook, errors);
        ValidateStatisticalField(index, nameof(StayGuestInput.TourismType), IstatGuestFieldLists.TourismTypeField, row.TourismType, errors);
        ValidateStatisticalField(index, nameof(StayGuestInput.TravelWay), IstatGuestFieldLists.TravelWayField, row.TravelWay, errors);
        ValidateStatisticalField(index, nameof(StayGuestInput.EducationLevel), IstatGuestFieldLists.EducationLevelField, row.EducationLevel, errors);

        if (!AlloggiatiRecordRules.RequiresDocument(row.Type))
            return;

        ValidateDocumentType(index, input, row, codeBook, errors);

        if (string.IsNullOrEmpty(row.DocumentNumber))
            errors.Add(Error(index, nameof(StayGuestInput.DocumentNumber), CheckInValidationKeys.FieldRequired));
        else if (!AlloggiatiRecordRules.IsValidDocumentNumber(row.DocumentNumber))
            errors.Add(Error(index, nameof(StayGuestInput.DocumentNumber), CheckInValidationKeys.DocumentNumberInvalid, AlloggiatiRecordRules.MaxDocumentNumberLength));

        ValidatePlace(index, nameof(StayGuestInput.DocumentIssuePlaceName), nameof(StayGuestInput.DocumentIssuePlaceCode),
            row.DocumentIssuePlaceName, row.DocumentIssuePlaceCode, [AlloggiatiCodeTable.Comuni, AlloggiatiCodeTable.Stati],
            codeBook, errors, entry => row.DocumentIssuePlaceName = entry.Description);

        ValidateDocumentDates(index, row, DateOnly.FromDateTime(today), errors);
    }

    /// <summary>
    /// The residence, when given: codes of the record's shape that exist in the imported table (a known code replaces the name with the
    /// official description, and the province for a comune), a two-letter province, names not longer than a label. Nothing is required.
    /// </summary>
    private static void ValidateResidence(int index, StayGuest row, AlloggiatiCodeBook codeBook, List<StayGuestFieldError> errors)
    {
        switch (row.ResidenceInItaly)
        {
            case true:
                ValidatePlace(index, nameof(StayGuestInput.ResidenceComuneName), nameof(StayGuestInput.ResidenceComuneCode),
                    row.ResidenceComuneName ?? string.Empty, row.ResidenceComuneCode, [AlloggiatiCodeTable.Comuni], codeBook, errors,
                    entry =>
                    {
                        row.ResidenceComuneName = entry.Description;
                        if (entry.Province is not null)
                            row.ResidenceProvince = entry.Province;
                    },
                    required: false);
                if (row.ResidenceProvince is not null && !AlloggiatiRecordRules.IsValidProvince(row.ResidenceProvince))
                    errors.Add(Error(index, nameof(StayGuestInput.ResidenceProvince), CheckInValidationKeys.ProvinceInvalid));
                break;
            case false:
                ValidatePlace(index, nameof(StayGuestInput.ResidenceCountryName), nameof(StayGuestInput.ResidenceCountryCode),
                    row.ResidenceCountryName ?? string.Empty, row.ResidenceCountryCode, [AlloggiatiCodeTable.Stati], codeBook, errors,
                    entry => row.ResidenceCountryName = entry.Description,
                    required: false);
                break;
        }
    }

    private static void ValidateStatisticalField(
        int index,
        string field,
        string listName,
        string? value,
        List<StayGuestFieldError> errors)
    {
        if (!IstatGuestFieldLists.IsValid(listName, value))
            errors.Add(Error(index, field, CheckInValidationKeys.StatisticalValueInvalid));
    }

    /// <summary>
    /// The document dates, when given: issued not after today (Europe/Rome) and not before 1900, expiring not before the issue. An expired
    /// document is accepted: the host records what the guest showed, the check-in only warns about it.
    /// </summary>
    private static void ValidateDocumentDates(int index, StayGuest row, DateOnly today, List<StayGuestFieldError> errors)
    {
        if (row.DocumentIssuedOn is { } issued && (issued > today || issued < EarliestDocumentDate))
            errors.Add(Error(index, nameof(StayGuestInput.DocumentIssuedOn), CheckInValidationKeys.DocumentIssuedOnInvalid));

        if (row.DocumentIssuedOn is { } issuedOn && row.DocumentExpiresOn is { } expires && expires < issuedOn)
            errors.Add(Error(index, nameof(StayGuestInput.DocumentExpiresOn), CheckInValidationKeys.DocumentExpiresBeforeIssue));
    }

    private static void ValidateDocumentType(
        int index,
        StayGuestInput input,
        StayGuest row,
        AlloggiatiCodeBook codeBook,
        List<StayGuestFieldError> errors)
    {
        if (row.DocumentTypeCode is { } code)
        {
            if (!AlloggiatiRecordRules.IsValidCodeShape(AlloggiatiCodeTable.Documenti, code))
                errors.Add(Error(index, nameof(StayGuestInput.DocumentTypeCode), CheckInValidationKeys.CodeInvalid));
            else if (codeBook.IsLoaded(AlloggiatiCodeTable.Documenti) && codeBook.FindCode(AlloggiatiCodeTable.Documenti, code) is null)
                errors.Add(Error(index, nameof(StayGuestInput.DocumentTypeCode), CheckInValidationKeys.CodeUnknown));
            return;
        }

        if (string.IsNullOrWhiteSpace(input.DocumentType))
            errors.Add(Error(index, nameof(StayGuestInput.DocumentType), CheckInValidationKeys.FieldRequired));
        else if (row.DocumentType is null)
            // "Other" (and any unknown kind) has no code in the official table: explicit error, never a guess.
            errors.Add(Error(index, nameof(StayGuestInput.DocumentType), CheckInValidationKeys.DocumentTypeInvalid));
    }

    /// <summary>
    /// A place or citizenship. Once its table is imported, a value that was typed must be one entry of that table
    /// (the code, or the only matching name; a citizenship adjective matches its country). An optional place
    /// (<paramref name="required"/> false) may stay empty.
    /// </summary>
    private static void ValidatePlace(
        int index,
        string nameField,
        string codeField,
        string name,
        string? code,
        AlloggiatiCodeTable[] tables,
        AlloggiatiCodeBook codeBook,
        List<StayGuestFieldError> errors,
        Action<AlloggiatiCodeEntry> useEntry,
        bool required = true)
    {
        if (code is not null && !tables.Any(table => AlloggiatiRecordRules.IsValidCodeShape(table, code)))
        {
            errors.Add(Error(index, codeField, CheckInValidationKeys.CodeInvalid));
            return;
        }

        if ((code is not null || !string.IsNullOrEmpty(name)) && tables.Any(codeBook.IsLoaded))
        {
            var lookup = ItalianCitizenshipNames.OfficialNormalizedKey(name) ?? name;
            var resolved = codeBook.Resolve(tables, code, lookup);
            if (resolved.Status == CodeResolutionStatus.Resolved)
            {
                var entry = tables.Select(table => codeBook.FindCode(table, resolved.Code)).FirstOrDefault(e => e is not null);
                if (entry is not null)
                {
                    useEntry(entry);
                    return;
                }
            }

            if (resolved.Status is CodeResolutionStatus.NotFound or CodeResolutionStatus.Ambiguous)
            {
                errors.Add(Error(index, code is null ? nameField : codeField, CheckInValidationKeys.CodeUnknown));
                return;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            if (required)
                errors.Add(Error(index, nameField, CheckInValidationKeys.FieldRequired));
        }
        else if (name.Length > MaxLabelLength)
        {
            errors.Add(Error(index, nameField, CheckInValidationKeys.FieldMaxLength, nameField, MaxLabelLength));
        }
    }

    private static void RequireText(int index, string field, string value, int maxLength, List<StayGuestFieldError> errors)
    {
        if (string.IsNullOrEmpty(value))
            errors.Add(Error(index, field, CheckInValidationKeys.FieldRequired));
        else if (value.Length > maxLength)
            errors.Add(Error(index, field, CheckInValidationKeys.FieldMaxLength, field, maxLength));
    }

    /// <summary>
    /// Copies the data of the input on the row: the input is the whole truth (a null clears the field), except the tourist tax exemption,
    /// copied only when <paramref name="copyTaxExemption"/> (a host author): the guest portal keeps the one of the row.
    /// </summary>
    private static void CopyData(StayGuest from, StayGuest to, bool copyTaxExemption)
    {
        to.Type = from.Type;
        to.FirstName = from.FirstName;
        to.LastName = from.LastName;
        to.Gender = from.Gender;
        to.DateOfBirth = from.DateOfBirth;
        to.BornInItaly = from.BornInItaly;
        to.BirthComuneCode = from.BirthComuneCode;
        to.BirthComuneName = from.BirthComuneName;
        to.BirthProvince = from.BirthProvince;
        to.BirthCountryCode = from.BirthCountryCode;
        to.BirthCountryName = from.BirthCountryName;
        to.CitizenshipCode = from.CitizenshipCode;
        to.CitizenshipName = from.CitizenshipName;
        to.DocumentType = from.DocumentType;
        to.DocumentTypeCode = from.DocumentTypeCode;
        to.DocumentNumber = from.DocumentNumber;
        to.DocumentIssuePlaceCode = from.DocumentIssuePlaceCode;
        to.DocumentIssuePlaceName = from.DocumentIssuePlaceName;
        to.DocumentIssuedOn = from.DocumentIssuedOn;
        to.DocumentExpiresOn = from.DocumentExpiresOn;
        to.ResidenceInItaly = from.ResidenceInItaly;
        to.ResidenceComuneCode = from.ResidenceComuneCode;
        to.ResidenceComuneName = from.ResidenceComuneName;
        to.ResidenceProvince = from.ResidenceProvince;
        to.ResidenceCountryCode = from.ResidenceCountryCode;
        to.ResidenceCountryName = from.ResidenceCountryName;
        to.TourismType = from.TourismType;
        to.TravelWay = from.TravelWay;
        to.EducationLevel = from.EducationLevel;
        if (copyTaxExemption)
            to.TaxExemptionCode = from.TaxExemptionCode;
    }

    private static string Clean(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static StayGuestFieldError Error(int index, string field, string key, params object[] args) =>
        new(index, field, key, args);
}
