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
        var (_, errors) = await BuildAsync(guests, cancellationToken);
        return errors;
    }

    public async Task<StayGuestSaveResult> ReplaceAsync(
        Booking booking,
        IReadOnlyList<StayGuestInput> guests,
        bool save = true,
        CancellationToken cancellationToken = default)
    {
        var (candidates, errors) = await BuildAsync(guests, cancellationToken);
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

            CopyData(candidates[position], row);
            // The first line stays linked to the booker, whose contact data and consent live on Guest.
            row.GuestId = position == 0 ? booking.GuestId : null;
            row.UpdatedAt = now;
            saved.Add(row);
        }

        db.StayGuests.RemoveRange(existing.Where(s => s.Position >= candidates.Count));

        if (save)
            await db.SaveChangesAsync(cancellationToken);

        return new StayGuestSaveResult(saved, []);
    }

    /// <summary>Normalized rows from the input, and the field errors that stop them from being saved.</summary>
    private async Task<(List<StayGuest> Candidates, List<StayGuestFieldError> Errors)> BuildAsync(
        IReadOnlyList<StayGuestInput> guests,
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

            candidates.Add(Normalize(input, type));
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

        return (candidates, errors);
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
        };

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
    /// A place or citizenship: the name, or a code of the record's shape that (once the table is imported) exists in it.
    /// A known code replaces the name with the official description.
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
        Action<AlloggiatiCodeEntry> useEntry)
    {
        if (code is not null)
        {
            if (!tables.Any(table => AlloggiatiRecordRules.IsValidCodeShape(table, code)))
            {
                errors.Add(Error(index, codeField, CheckInValidationKeys.CodeInvalid));
                return;
            }

            var entry = tables.Select(table => codeBook.FindCode(table, code)).FirstOrDefault(e => e is not null);
            if (entry is not null)
            {
                useEntry(entry);
                return;
            }

            if (tables.Any(codeBook.IsLoaded))
            {
                errors.Add(Error(index, codeField, CheckInValidationKeys.CodeUnknown));
                return;
            }
        }

        if (string.IsNullOrEmpty(name))
            errors.Add(Error(index, nameField, CheckInValidationKeys.FieldRequired));
        else if (name.Length > MaxLabelLength)
            errors.Add(Error(index, nameField, CheckInValidationKeys.FieldMaxLength, nameField, MaxLabelLength));
    }

    private static void RequireText(int index, string field, string value, int maxLength, List<StayGuestFieldError> errors)
    {
        if (string.IsNullOrEmpty(value))
            errors.Add(Error(index, field, CheckInValidationKeys.FieldRequired));
        else if (value.Length > maxLength)
            errors.Add(Error(index, field, CheckInValidationKeys.FieldMaxLength, field, maxLength));
    }

    private static void CopyData(StayGuest from, StayGuest to)
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
    }

    private static string Clean(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static StayGuestFieldError Error(int index, string field, string key, params object[] args) =>
        new(index, field, key, args);
}
