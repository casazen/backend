using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Regulatory;

/// <summary>Where the guests of a stay are, for the check-in wizard (E03/F01/US01/T04, NF-45).</summary>
public enum StayAttendance
{
    /// <summary>Confirmed, the arrival not registered and no no-show.</summary>
    Expected = 0,

    /// <summary>The arrival is registered (<see cref="BookingStatus.CheckedIn"/>).</summary>
    Arrived = 1,

    /// <summary>The stay is checked out.</summary>
    Departed = 2,

    /// <summary>Confirmed, with <see cref="Booking.NoShowAt"/> set and no arrival: the guests never came.</summary>
    NoShow = 3,

    /// <summary>Pending or cancelled: no check-in is expected.</summary>
    Inactive = 4,
}

/// <summary>The attendance of a stay, from its status and its no-show mark.</summary>
public static class CheckInAttendance
{
    public static StayAttendance Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.Status switch
        {
            BookingStatus.CheckedIn => StayAttendance.Arrived,
            BookingStatus.CheckedOut => StayAttendance.Departed,
            BookingStatus.Confirmed when booking.NoShowAt is not null => StayAttendance.NoShow,
            BookingStatus.Confirmed => StayAttendance.Expected,
            _ => StayAttendance.Inactive,
        };
    }
}

/// <summary>
/// Something the check-in wizard shows on the data of the stay, in a code the client translates. A blocking issue stops the confirmation of the
/// wizard (never the arrival registration); the others are warnings.
/// </summary>
/// <param name="Position">The position of the guest, or null for the stay as a whole.</param>
/// <param name="Code">One of <see cref="CheckInIssueCodes"/>.</param>
/// <param name="Field">The camelCase field of the guest it is about (the names of <see cref="AlloggiatiRecordRules"/>), or null.</param>
/// <param name="Blocking">True when the wizard cannot be confirmed until it is fixed.</param>
public sealed record CheckInIssue(int? Position, string Code, string? Field, bool Blocking);

/// <summary>The codes of <see cref="CheckInIssue"/>.</summary>
public static class CheckInIssueCodes
{
    /// <summary>A field of the Alloggiati record is missing or not accepted (blocking).</summary>
    public const string GuestFieldMissing = "guest_field_missing";

    /// <summary>The official code of a field is still to complete: it blocks only the record file (a warning here).</summary>
    public const string GuestCodeMissing = "guest_code_missing";

    /// <summary>The booking is mirrored on Octorate and a name Octorate requires of a complete guest is missing (blocking).</summary>
    public const string OctorateFieldMissing = "octorate_field_missing";

    /// <summary>
    /// A statistical field the region requires is empty (a warning, never blocking): the ISTAT statistic is not a reason to hold the guests back from the
    /// police communication, which is a legal obligation with a deadline (<see cref="CheckInReadinessRules.SendBlockers"/>).
    /// </summary>
    public const string GuestFieldRequired = "guest_field_required";

    /// <summary>The document expires before the arrival: a warning, the host records what the guest showed.</summary>
    public const string DocumentExpired = "document_expired";

    /// <summary>The registered guests are not as many as the booking declares (a warning).</summary>
    public const string GuestsCountMismatch = "guests_count_mismatch";
}

/// <summary>What <see cref="CheckInReadinessRules.Evaluate"/> looks at.</summary>
/// <param name="Guests">The registered guests of the stay, in record order (the unsaved booker fallback is not one).</param>
/// <param name="Codes">The official codes of each guest, in the same order; null when they were not resolved.</param>
/// <param name="Arrival">The arrival date of the stay.</param>
/// <param name="DeclaredGuests">The guests the booking declares.</param>
/// <param name="OctorateReceivesGuests">True when the booking is mirrored on Octorate and its guests are sent there.</param>
/// <param name="RequiredGuestFields">The statistical fields the region requires (<see cref="IstatGuestFieldLists"/> names); empty when none.</param>
/// <param name="InTicino">
/// The property is in canton Ticino (NF-132): every guest the police file notifies on their own row (<see cref="TicinoPoliceFile.NotifiedPositions"/>)
/// needs a document type and number, not only the head of the stay.
/// </param>
public sealed record CheckInReadinessInput(
    IReadOnlyList<StayGuest> Guests,
    IReadOnlyList<StayGuestCodes>? Codes,
    DateOnly Arrival,
    int DeclaredGuests,
    bool OctorateReceivesGuests,
    IReadOnlyCollection<string> RequiredGuestFields,
    bool InTicino = false);

/// <summary>
/// The checks of the check-in wizard (E03/F01/US01/T04, NF-40, NF-45), pure: no database, no call. The completeness of the Alloggiati record is
/// the one of <see cref="AlloggiatiRecordRules"/>, reused and never rewritten. A field is asked only where the record, Octorate (a mirrored
/// booking) or the region requires it (AC-NF40-1: data minimization).
/// </summary>
public static class CheckInReadinessRules
{
    /// <summary>The stay has no registered guest yet.</summary>
    public const string FieldGuests = "guests";

    public const string FieldResidence = "residence";
    public const string FieldResidenceComune = "residenceComune";
    public const string FieldResidenceCountry = "residenceCountry";
    public const string FieldDocumentExpiresOn = "documentExpiresOn";

    /// <summary>A no-show before the check-in day (422).</summary>
    public const string NoShowTooEarlyCode = "no_show_too_early";

    /// <summary>A no-show of a stay whose police communication already went (409).</summary>
    public const string NoShowAlloggiatiSentCode = "no_show_alloggiati_sent";

    /// <summary>The fields of the document of a guest (the documents step of the wizard).</summary>
    public static IReadOnlySet<string> DocumentFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AlloggiatiRecordRules.FieldDocumentType,
        AlloggiatiRecordRules.FieldDocumentNumber,
        AlloggiatiRecordRules.FieldDocumentIssuePlace,
        FieldDocumentExpiresOn,
    };

    /// <summary>
    /// The issues that keep the guests of a stay from going to the channel manager, which sends the police communication on: the blocking ones, and
    /// <b>never</b> a statistical field a region requires (<see cref="CheckInIssueCodes.GuestFieldRequired"/>, even if one were ever built as blocking). The
    /// Alloggiati communication is a legal obligation with a deadline; the ISTAT statistic is not, and CasaZen does not pass those fields to the channel
    /// manager. A missing one stays a warning in the wizard and holds nothing back. The one rule of the confirmation and the next step of the wizard and of
    /// the automatic send, so the two never disagree on when a stay is ready.
    /// </summary>
    public static IReadOnlyList<CheckInIssue> SendBlockers(IEnumerable<CheckInIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return [.. issues.Where(i => i.Blocking && i.Code != CheckInIssueCodes.GuestFieldRequired)];
    }

    /// <summary>The issues of the stay: blocking ones first per guest, then the warnings, then the stay as a whole.</summary>
    public static IReadOnlyList<CheckInIssue> Evaluate(CheckInReadinessInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = new List<CheckInIssue>();
        var guests = input.Guests;
        if (guests.Count == 0)
        {
            issues.Add(new CheckInIssue(null, CheckInIssueCodes.GuestFieldMissing, FieldGuests, Blocking: true));
            return issues;
        }

        foreach (var error in AlloggiatiRecordRules.CompositionErrors(guests.Select(g => g.Type).ToList()))
            issues.Add(new CheckInIssue(guests[error.Index].Position, CheckInIssueCodes.GuestFieldMissing, AlloggiatiRecordRules.FieldType, true));

        // NF-132: in Ticino the police file notifies every adult (or every minor of a stay without adults) on their own row, with the type of their
        // document: the check-in is not complete until each of them has one, family and group members included.
        var notified = input.InTicino
            ? TicinoPoliceFile.NotifiedPositions(
                guests.Select(g => (g.Position, g.Type, g.DateOfBirth is { } born ? DateOnly.FromDateTime(born) : (DateOnly?)null)), input.Arrival)
            : null;

        for (var index = 0; index < guests.Count; index++)
        {
            var guest = guests[index];
            var missing = AlloggiatiRecordRules.MissingFields(guest);
            issues.AddRange(missing.Select(field => new CheckInIssue(guest.Position, CheckInIssueCodes.GuestFieldMissing, field, true)));

            if (notified is not null && notified.Contains(guest.Position))
                issues.AddRange(TicinoDocumentIssues(guest, missing));

            if (input.OctorateReceivesGuests)
                issues.AddRange(OctorateIssues(guest, missing));

            issues.AddRange(RequiredIssues(guest, input.RequiredGuestFields));

            if (input.Codes is { } codes && index < codes.Count)
            {
                issues.AddRange(codes[index].CodesToComplete
                    .Select(field => new CheckInIssue(guest.Position, CheckInIssueCodes.GuestCodeMissing, field, false)));
            }

            if (AlloggiatiRecordRules.RequiresDocument(guest.Type) && guest.DocumentExpiresOn is { } expires && expires < input.Arrival)
                issues.Add(new CheckInIssue(guest.Position, CheckInIssueCodes.DocumentExpired, FieldDocumentExpiresOn, false));
        }

        if (input.DeclaredGuests > 0 && guests.Count != input.DeclaredGuests)
            issues.Add(new CheckInIssue(null, CheckInIssueCodes.GuestsCountMismatch, null, false));

        return issues;
    }

    /// <summary>
    /// The error that stops marking the stay as a no-show, or null (NF-45): only a <see cref="BookingStatus.Confirmed"/> booking (409
    /// <see cref="BookingErrorCodes.NotConfirmed"/>), from its check-in day in Europe/Rome (422 <see cref="NoShowTooEarlyCode"/>), and never once
    /// the police communication went (409 <see cref="NoShowAlloggiatiSentCode"/>: the host corrects it on the portal).
    /// </summary>
    public static DomainException? NoShowError(Booking booking, DateTime todayInRome, bool alloggiatiSent)
    {
        ArgumentNullException.ThrowIfNull(booking);
        if (booking.Status != BookingStatus.Confirmed)
            return new DomainConflictException(BookingErrorCodes.NotConfirmed, "BookingNoShowNotConfirmed");
        if (todayInRome.Date < booking.CheckInDate.Date)
            return new DomainRuleException(NoShowTooEarlyCode, "BookingNoShowTooEarly");
        if (alloggiatiSent)
            return new DomainConflictException(NoShowAlloggiatiSentCode, "BookingNoShowAlloggiatiSent");
        return null;
    }

    /// <summary>
    /// The names Octorate requires of a complete guest (sandbox, 2026-11-04): the comune of birth of a guest born in Italy and the place of
    /// residence. A field the record already reports missing is not reported twice.
    /// </summary>
    private static IEnumerable<CheckInIssue> OctorateIssues(StayGuest guest, IReadOnlyList<string> missing)
    {
        if (guest.BornInItaly == true
            && string.IsNullOrWhiteSpace(guest.BirthComuneName)
            && !missing.Contains(AlloggiatiRecordRules.FieldBirthComune))
        {
            yield return new CheckInIssue(guest.Position, CheckInIssueCodes.OctorateFieldMissing, AlloggiatiRecordRules.FieldBirthComune, true);
        }

        var residence = guest.ResidenceInItaly switch
        {
            null => FieldResidence,
            true when string.IsNullOrWhiteSpace(guest.ResidenceComuneName) => FieldResidenceComune,
            false when string.IsNullOrWhiteSpace(guest.ResidenceCountryName) => FieldResidenceCountry,
            _ => null,
        };
        if (residence is not null)
            yield return new CheckInIssue(guest.Position, CheckInIssueCodes.OctorateFieldMissing, residence, true);
    }

    /// <summary>
    /// The document of a guest the Ticino police file notifies (NF-132): its type (the kind chosen or the code of the table) and its number, blocking. A field
    /// the Alloggiati record already reports missing (a single guest or a head) is not reported twice.
    /// </summary>
    private static IEnumerable<CheckInIssue> TicinoDocumentIssues(StayGuest guest, IReadOnlyList<string> missing)
    {
        var hasKind = guest.DocumentType is GuestDocumentType.Passport or GuestDocumentType.IdentityCard or GuestDocumentType.DriversLicense;
        if (!hasKind && string.IsNullOrWhiteSpace(guest.DocumentTypeCode) && !missing.Contains(AlloggiatiRecordRules.FieldDocumentType))
            yield return new CheckInIssue(guest.Position, CheckInIssueCodes.GuestFieldMissing, AlloggiatiRecordRules.FieldDocumentType, true);

        if (!AlloggiatiRecordRules.IsValidDocumentNumber(AlloggiatiRecordRules.NormalizeDocumentNumber(guest.DocumentNumber))
            && !missing.Contains(AlloggiatiRecordRules.FieldDocumentNumber))
        {
            yield return new CheckInIssue(guest.Position, CheckInIssueCodes.GuestFieldMissing, AlloggiatiRecordRules.FieldDocumentNumber, true);
        }
    }

    /// <summary>
    /// The statistical fields the region requires and the guest left empty, as warnings (never blocking, see <see cref="SendBlockers"/>); a field the region
    /// does not require is never asked.
    /// </summary>
    private static IEnumerable<CheckInIssue> RequiredIssues(StayGuest guest, IReadOnlyCollection<string> required)
    {
        foreach (var field in required)
        {
            var value = field switch
            {
                IstatGuestFieldLists.TourismTypeField => guest.TourismType,
                IstatGuestFieldLists.TravelWayField => guest.TravelWay,
                IstatGuestFieldLists.EducationLevelField => guest.EducationLevel,
                _ => null,
            };
            var known = field is IstatGuestFieldLists.TourismTypeField or IstatGuestFieldLists.TravelWayField or IstatGuestFieldLists.EducationLevelField;
            if (known && string.IsNullOrWhiteSpace(value))
                yield return new CheckInIssue(guest.Position, CheckInIssueCodes.GuestFieldRequired, CamelCase(field), Blocking: false);
        }
    }

    private static string CamelCase(string field) => field.Length == 0 ? field : char.ToLowerInvariant(field[0]) + field[1..];
}
