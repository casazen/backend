using Casazen.Core.Utilities;

namespace Casazen.Core.Regulatory.GuestRegistration;

/// <summary>The kinds of document the assisted notification names (the codes the clients translate); written as they are in the JSON.</summary>
public static class SwissDocumentTypes
{
    public const string Passport = "passport";
    public const string IdentityCard = "identityCard";
    public const string Other = "other";
}

/// <summary>
/// One guest of an arrival day as the host copies it onto the portal of the canton (E31/F02/US01/T01, NF-131): what the check-in recorded, written the way the
/// portals ask it, never more than the notification needs (no exemption, no photograph of the document, no contact of the booker). Every text is trimmed and empty
/// text is <c>null</c>, so a field that is not there is not there.
/// </summary>
/// <param name="BookingId">The stay.</param>
/// <param name="BookingCode">The code of the booking as hosts read it (<c>XXXXX-XXXXX</c>), to find the stay again; <c>null</c> when the booking has none.</param>
/// <param name="Position">Order of the guest in the stay, from 0.</param>
/// <param name="Arrival">The arrival day of the stay.</param>
/// <param name="Departure">The departure day of the stay.</param>
/// <param name="FirstName">First name; <c>null</c> when the check-in has none.</param>
/// <param name="LastName">Last name; <c>null</c> when the check-in has none.</param>
/// <param name="BirthDate">Date of birth; <c>null</c> when the check-in has none.</param>
/// <param name="PlaceOfBirth">The comune of birth (born in Italy) or the country of birth (born abroad), as entered; <c>null</c> when not entered.</param>
/// <param name="Nationality">The nationality as the guest entered it; <c>null</c> when not entered.</param>
/// <param name="NationalityCode">The nationality as ISO 3166-1 alpha-3 (<c>ITA</c>, <c>DEU</c>), where the check-in lets CasaZen tell it; <c>null</c> otherwise.</param>
/// <param name="DocumentType">One of <see cref="SwissDocumentTypes"/>; <c>null</c> when the check-in has no document type.</param>
/// <param name="DocumentNumber">The number of the document, as recorded (encrypted at rest); <c>null</c> when not entered.</param>
/// <param name="Street">Street and number of the home; <c>null</c> when not entered.</param>
/// <param name="PostalCode">Postal code of the home, as written; <c>null</c> when not entered.</param>
/// <param name="City">Town of the home, as written; <c>null</c> when not entered.</param>
/// <param name="ResidenceCountryCode">The country of the home as ISO alpha-3 (<c>ITA</c> for a home in Italy); <c>null</c> when not known.</param>
/// <param name="Plate">The plate of the car of the stay; <c>null</c> when the stay has none.</param>
public sealed record SwissNotificationGuest(
    Guid BookingId,
    string? BookingCode,
    int Position,
    DateOnly Arrival,
    DateOnly Departure,
    string? FirstName,
    string? LastName,
    DateOnly? BirthDate,
    string? PlaceOfBirth,
    string? Nationality,
    string? NationalityCode,
    string? DocumentType,
    string? DocumentNumber,
    string? Street,
    string? PostalCode,
    string? City,
    string? ResidenceCountryCode,
    string? Plate);

/// <summary>
/// What a guest (or a whole stay, <see cref="Position"/> <c>null</c>) still lacks to be copied onto the portal: the codes of <see cref="TicinoMissing"/>, the same the Ticino day
/// and the alerts use, so the clients translate one list. The guest stays in the list: the host can type what the check-in has and complete the rest.
/// </summary>
/// <param name="BookingId">The stay.</param>
/// <param name="Position">The guest, or <c>null</c> for the stay as a whole.</param>
/// <param name="GuestName">The name of the guest, or of the booker for a stay with no guest recorded.</param>
/// <param name="Missing">The codes of what is missing: <c>firstName</c>, <c>lastName</c>, <c>birthDate</c>, <c>nationality</c>, <c>documentType</c>, <c>guests</c>.</param>
public sealed record SwissNotificationIssue(Guid BookingId, int? Position, string GuestName, IReadOnlyList<string> Missing);

/// <summary>
/// The assisted notification of the guests to the authority of a canton other than Ticino (E31/F02/US01/T01, NF-131, decisions D1, D5 and D12): the rules that say which
/// regimes it covers, when the notification of an arrival day is due and what the data of the day lack. Pure: no database, no clock. CasaZen transmits nothing (decision D1):
/// it prepares the data and the deadline, the host copies them onto the official portal and declares the day.
/// </summary>
/// <remarks>
/// <para><b>Which regimes.</b> A Swiss regime other than Ticino (its own section and file) whose obligation is to report to an authority and for which CasaZen's support is the
/// assisted notification (<see cref="Covers"/>): today Basel-Stadt, Fribourg, Geneva, Neuchâtel, Nidwalden, Schaffhausen, Thurgau, Zug and Zurich. A canton that only asks the host to
/// keep a register is the register's (E31/F01), whatever the host says. Whether the duty is <i>raised</i> is another rule (<see cref="GuestRegistrationDutyRules"/>): a duty that is
/// <c>Ask</c> or <c>NotRequired</c> never raises an item or an alert.</para>
/// <para><b>When it is due</b> (<see cref="DueAtUtc"/>, on the clock of the property, a deadline is never guessed): <see cref="GuestRegistrationDeadlineKind.AtArrival"/> is the
/// arrival moment (the first arrival recorded among the stays of the day, else the end of the arrival day); <see cref="GuestRegistrationDeadlineKind.DailyByTime"/> is the day after the
/// arrival at the time of the regime (Schaffhausen 02:00, whose brochure does not say which day the time means; a regime with no time: the end of the arrival day);
/// <see cref="GuestRegistrationDeadlineKind.Weekly"/> is Sunday 23:59 of the arrival week (Nidwalden); <see cref="GuestRegistrationDeadlineKind.None"/> has no deadline and so no
/// alert. <see cref="GuestRegistrationDeadlineKind.Hours24FromArrival"/> does not occur here (Ticino and Italy have their own flows): the rule is the one of the Ticino
/// notification, so it is total.</para>
/// </remarks>
public static class SwissAssistedNotification
{
    /// <summary>How many days back the days still to notify are looked for (the window of the Ticino days).</summary>
    public const int PendingLookbackDays = 14;

    /// <summary>The time of the Sunday at which the weekly deadline of a canton falls (Nidwalden): the last minute of the week.</summary>
    private static readonly TimeOnly LastMinuteOfTheWeek = new(23, 59);

    /// <summary>
    /// The regime is covered by the assisted notification: Swiss, not Ticino, an obligation to report to an authority, and CasaZen's support is the assisted notification.
    /// <c>false</c> for <c>null</c> (a country the catalog does not know).
    /// </summary>
    public static bool Covers(GuestRegistrationRegime? regime) =>
        regime is { IsSwiss: true, Obligation: GuestRegistrationObligation.ReportToAuthority, CasaZenSupport: GuestRegistrationSupport.AssistedNotification }
        && !string.Equals(regime.Code, GuestRegistrationRegimeCatalog.TicinoCode, StringComparison.Ordinal);

    /// <summary>
    /// The instant the notification of <paramref name="arrivalDay"/> is due (UTC), or <c>null</c> when the regime has no deadline. <paramref name="firstArrivalUtc"/> is the
    /// earliest arrival recorded among the stays of the day that owe a notification, <c>null</c> when none is recorded.
    /// </summary>
    public static DateTime? DueAtUtc(GuestRegistrationRegime regime, DateOnly arrivalDay, TimeZoneInfo propertyZone, DateTime? firstArrivalUtc)
    {
        ArgumentNullException.ThrowIfNull(regime);
        ArgumentNullException.ThrowIfNull(propertyZone);

        return regime.DeadlineKind switch
        {
            GuestRegistrationDeadlineKind.AtArrival => firstArrivalUtc is { } arrived ? AsUtc(arrived) : EndOfDayUtc(arrivalDay, propertyZone),
            GuestRegistrationDeadlineKind.DailyByTime => RomeCalendar.ToUtc(arrivalDay.AddDays(1), regime.DeadlineTime ?? TimeOnly.MinValue, propertyZone),
            GuestRegistrationDeadlineKind.Weekly => RomeCalendar.ToUtc(SundayOf(arrivalDay), LastMinuteOfTheWeek, propertyZone),
            GuestRegistrationDeadlineKind.Hours24FromArrival => firstArrivalUtc is { } first ? AsUtc(first).AddHours(24) : EndOfDayUtc(arrivalDay, propertyZone),
            _ => null,
        };
    }

    /// <summary>The Sunday of the week of <paramref name="day"/> (weeks run Monday to Sunday); a Sunday is its own.</summary>
    public static DateOnly SundayOf(DateOnly day) => day.AddDays((7 - (int)day.DayOfWeek) % 7);

    /// <summary>
    /// The guests of the stays of one arrival day as the host copies them, and what they lack. Every guest of every stay is listed (the order of the stays, then of the check-in), a
    /// guest with a gap is never left out: the host types what is there and completes the rest. <paramref name="today"/> is the last birth date accepted.
    /// </summary>
    /// <remarks>
    /// What every guest needs: a first name, a last name, a date of birth (from 01.01.1900 to <paramref name="today"/>) and a nationality (as entered, or a code CasaZen can tell). An
    /// adult at the arrival (18 or more), or a guest whose age is not known, also needs the type of document. A stay with fewer guests recorded than the booking counts, or none, lacks
    /// <see cref="TicinoMissing.Guests"/>. Address, plate and place of birth are never required: the portals of the cantons are not verified to ask for them.
    /// </remarks>
    public static (IReadOnlyList<SwissNotificationGuest> Guests, IReadOnlyList<SwissNotificationIssue> Issues) Plan(IEnumerable<TicinoStayFacts> stays, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(stays);

        var guests = new List<SwissNotificationGuest>();
        var issues = new List<SwissNotificationIssue>();
        foreach (var stay in stays.OrderBy(s => s.Arrival).ThenBy(s => s.BookingId))
        {
            var ordered = stay.Guests.OrderBy(g => g.Position).ToList();
            if (ordered.Count == 0 || ordered.Count < stay.NumberOfGuests)
                issues.Add(new SwissNotificationIssue(stay.BookingId, null, stay.BookerName, [TicinoMissing.Guests]));

            foreach (var guest in ordered)
            {
                guests.Add(GuestOf(stay, guest));
                if (MissingOf(stay, guest, today) is { Count: > 0 } missing)
                    issues.Add(new SwissNotificationIssue(stay.BookingId, guest.Position, NameOf(guest, stay.BookerName), missing));
            }
        }

        return (guests, issues);
    }

    private static SwissNotificationGuest GuestOf(TicinoStayFacts stay, TicinoGuestFacts guest) => new(
        stay.BookingId,
        TextOf(stay.ReferenceNumber),
        guest.Position,
        stay.Arrival,
        stay.Departure,
        TextOf(guest.FirstName),
        TextOf(guest.LastName),
        guest.BirthDate,
        TextOf(guest.BornInItaly == true ? guest.BirthComuneName : guest.BirthCountryName),
        TextOf(guest.CitizenshipName),
        TicinoCountryCodes.FromCheckIn(guest.CitizenshipCode, guest.CitizenshipName),
        DocumentTypeOf(guest),
        TextOf(guest.DocumentNumber),
        TextOf(guest.Street),
        TextOf(guest.PostalCode),
        TextOf(guest.ResidenceInItaly == true && !string.IsNullOrWhiteSpace(guest.ResidenceComuneName) ? guest.ResidenceComuneName : guest.City),
        guest.ResidenceInItaly == true ? "ITA" : TicinoCountryCodes.FromCheckIn(guest.ResidenceCountryCode, guest.ResidenceCountryName),
        TextOf(stay.VehiclePlate));

    private static List<string> MissingOf(TicinoStayFacts stay, TicinoGuestFacts guest, DateOnly today)
    {
        var missing = new List<string>();
        if (TextOf(guest.FirstName) is null)
            missing.Add(TicinoMissing.FirstName);
        if (TextOf(guest.LastName) is null)
            missing.Add(TicinoMissing.LastName);

        var validBirthDate = guest.BirthDate is { } born && born >= TicinoPoliceFile.MinBirthDate && born <= today;
        if (!validBirthDate)
            missing.Add(TicinoMissing.BirthDate);

        if (TextOf(guest.CitizenshipName) is null && TicinoCountryCodes.FromCheckIn(guest.CitizenshipCode, guest.CitizenshipName) is null)
            missing.Add(TicinoMissing.Nationality);

        // A minor does not need a document; an adult, or a guest whose age is not known (the date of birth is asked first), does.
        var minor = validBirthDate && TicinoPoliceFile.AgeAt(guest.BirthDate!.Value, stay.Arrival) < TicinoPoliceFile.AdultAge;
        if (!minor && DocumentTypeOf(guest) is null)
            missing.Add(TicinoMissing.DocumentType);

        return missing;
    }

    private static string? DocumentTypeOf(TicinoGuestFacts guest) => TicinoPoliceFile.DocumentKindOf(guest) switch
    {
        TicinoDocumentKind.Passport => SwissDocumentTypes.Passport,
        TicinoDocumentKind.IdentityCard => SwissDocumentTypes.IdentityCard,
        TicinoDocumentKind.Other => SwissDocumentTypes.Other,
        _ => null,
    };

    private static string NameOf(TicinoGuestFacts guest, string bookerName)
    {
        var name = $"{TextOf(guest.FirstName)} {TextOf(guest.LastName)}".Trim();
        return name.Length > 0 ? name : bookerName;
    }

    /// <summary>Trimmed, inner runs of white space as one space; <c>null</c> when there is nothing.</summary>
    private static string? TextOf(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DateTime EndOfDayUtc(DateOnly day, TimeZoneInfo zone) => RomeCalendar.ToUtc(day.AddDays(1), TimeOnly.MinValue, zone);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}
