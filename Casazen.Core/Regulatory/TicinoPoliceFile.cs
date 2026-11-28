using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Regulatory;

public static partial class TicinoStructureId
{
    public const int MaxLength = 20;

    public const int MinLength = 3;

    public const string Pattern = "^[A-Z0-9]+(-[A-Z0-9]+)*$";

    public const string InvalidFormatMessageKey = "TicinoStructureIdInvalid";

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return compact.Length == 0 ? null : compact;
    }

    public static bool IsValid(string? value) =>
        Normalize(value) is { Length: >= MinLength and <= MaxLength } normalized && PatternRegex().IsMatch(normalized);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex PatternRegex();
}

public enum TicinoPoliceFileFormat
{
    Csv = 1,

    Xml = 2,
}

public enum TicinoDocumentKind
{
    Passport = 1,
    IdentityCard = 2,
    Other = 3,
}

public enum TicinoCsvCharset
{
    Windows1252 = 1,

    Utf8 = 2,
}

public enum TicinoRowKind
{
    Guest = 1,

    Group = 2,
}

public sealed record TicinoGroupMember(string FirstName, string LastName, DateOnly? BirthDate, string Nationality);

public sealed record TicinoStayWarning(Guid BookingId, string Code);

public static class TicinoWarnings
{
    public const string ParentalAuthorizationNeeded = "parental_authorization_needed";

    public const string GroupSheetIncomplete = "group_sheet_incomplete";
}

public sealed record TicinoCsvFile(byte[] Content, string Charset, int TransliteratedCount);

public sealed record TicinoGuestFacts(
    int Position,
    StayGuestType Type,
    string? FirstName,
    string? LastName,
    DateOnly? BirthDate,
    string? CitizenshipCode,
    string? CitizenshipName,
    bool? BornInItaly,
    string? BirthComuneName,
    string? BirthCountryName,
    GuestDocumentType? DocumentType,
    string? DocumentTypeCode,
    string? DocumentNumber,
    bool? ResidenceInItaly,
    string? ResidenceComuneName,
    string? ResidenceCountryCode,
    string? ResidenceCountryName,
    string? Street,
    string? PostalCode,
    string? City)
{
    /// <summary>
    /// A guest of the stay as the file reads it, from the row of the check-in (the one reading of a <see cref="StayGuest"/> for the Ticino file and for the guest register
    /// of E31/F01). The home is the one the check-in recorded for the guest (NF-132); for the row linked to the booker's record, a part the check-in does not have is taken
    /// from that record, as before NF-132.
    /// </summary>
    /// <param name="guest">The row of the check-in.</param>
    /// <param name="record">The guest record the row is linked to (the booker's), when it is; <c>null</c> otherwise.</param>
    public static TicinoGuestFacts From(StayGuest guest, Guest? record)
    {
        ArgumentNullException.ThrowIfNull(guest);

        return new TicinoGuestFacts(
            guest.Position,
            guest.Type,
            guest.FirstName,
            guest.LastName,
            guest.DateOfBirth is { } born ? DateOnly.FromDateTime(born) : null,
            guest.CitizenshipCode,
            guest.CitizenshipName,
            guest.BornInItaly,
            guest.BirthComuneName,
            guest.BirthCountryName,
            guest.DocumentType,
            guest.DocumentTypeCode,
            guest.DocumentNumber,
            guest.ResidenceInItaly,
            guest.ResidenceComuneName,
            guest.ResidenceCountryCode,
            guest.ResidenceCountryName,
            FirstText(guest.ResidenceStreet, record?.Address),
            FirstText(guest.ResidencePostalCode, record?.PostalCode),
            FirstText(guest.ResidenceCityName, record?.City));
    }

    private static string? FirstText(string? recorded, string? fallback) => string.IsNullOrWhiteSpace(recorded) ? fallback : recorded;
}

/// <summary>A stay that arrives on the day of the file, with the guests the check-in recorded (in their order).</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="BookerName">The booker, for the issues of a stay with no guest recorded.</param>
/// <param name="Arrival">The arrival day.</param>
/// <param name="Departure">The departure day.</param>
/// <param name="NumberOfGuests">The guests the booking counts.</param>
/// <param name="Guests">The guests of the check-in, in their order.</param>
/// <param name="ReferenceNumber">«Numero bollettino»: the code of the booking as hosts read it (<c>XXXXX-XXXXX</c>), on every row of the stay.</param>
/// <param name="VehiclePlate">«Numero autovettura»: the plate of the stay, on the row of its head only.</param>
public sealed record TicinoStayFacts(
    Guid BookingId,
    string BookerName,
    DateOnly Arrival,
    DateOnly Departure,
    int NumberOfGuests,
    IReadOnlyList<TicinoGuestFacts> Guests,
    string? ReferenceNumber = null,
    string? VehiclePlate = null)
{
    public static TicinoStayFacts From(Booking booking, IEnumerable<StayGuest> stayGuests)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(stayGuests);

        var rows = stayGuests.OrderBy(g => g.Position).ToList();
        var guests = rows.Count > 0
            ? rows.Select(g => TicinoGuestFacts.From(g, g.Guest)).ToList()
            : [TicinoGuestFacts.From(StayGuest.FromBooker(booking, booking.Guest), booking.Guest)];
        var booker = $"{booking.Guest?.FirstName} {booking.Guest?.LastName}".Trim();

        return new TicinoStayFacts(
            booking.Id,
            booker,
            DateOnly.FromDateTime(booking.CheckInDate),
            DateOnly.FromDateTime(booking.CheckOutDate),
            booking.NumberOfGuests,
            guests,
            string.IsNullOrWhiteSpace(booking.BookingCode) ? null : Services.BookingCodes.Format(booking.BookingCode),
            booking.VehiclePlate);
    }
}

public sealed record TicinoRegistration(
    Guid BookingId,
    int Position,
    string UserId,
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    string Nationality,
    DateOnly ArrivalDate,
    string PlaceOfBirth,
    string Street,
    string Zip,
    string City,
    string Nation,
    DateOnly DepartureDate,
    int PersonsOver14,
    int PersonsUnder14,
    TicinoDocumentKind DocumentKind,
    string DocumentNumber)
{
    /// <summary>«Numero autovettura» / <c>carid</c>: the plate of the stay on the row of its head (the person the minors are counted on), empty otherwise.</summary>
    public string CarId { get; init; } = string.Empty;

    public string ReferenceNr { get; init; } = string.Empty;

    public TicinoRowKind Kind { get; init; } = TicinoRowKind.Guest;

    public int GroupSize { get; init; }

    public bool FitsXml => Zip.Length is > 0 and <= 18 && Zip.All(char.IsAsciiDigit) && Nation.Length == 3;
}

public sealed record TicinoGuestIssue(Guid BookingId, int? Position, string GuestName, IReadOnlyList<string> Missing);

public static class TicinoMissing
{
    public const string FirstName = "firstName";
    public const string LastName = "lastName";
    public const string BirthDate = "birthDate";
    public const string Nationality = "nationality";
    public const string DocumentType = "documentType";

    public const string Guests = "guests";
}

public sealed record TicinoDayPlan(IReadOnlyList<TicinoRegistration> Registrations, IReadOnlyList<TicinoGuestIssue> Issues)
{
    public IReadOnlyList<TicinoStayWarning> Warnings { get; init; } = [];

    /// <summary>The XML can be written: there is a row and every row fits the XSD (otherwise the CSV, which has no such limits).</summary>
    public bool XmlAvailable => Registrations.Count > 0 && Registrations.All(r => r.FitsXml);
}

/// <summary>
/// The file of the guests of a Ticino property for the cantonal police (notifiche.ti.ch, «Notifiche → Invio»; casazen/backend#979), in the
/// official formats of <c>https://www4.ti.ch/index.php?id=145950</c>. CasaZen only writes the file: the host uploads it on the portal (login and
/// TOTP, no API) within 24 hours of the arrival and records the receipt.
/// </summary>
/// <remarks>
/// <para><b>Who is a row.</b> Every adult (18 or more at the arrival) is notified; the minors of a stay are counted on ONE adult of it (the
/// head of family or group, otherwise the first adult) in «Mag14» (14 to 17) and «Min14» (under 14), and the notified person is never counted
/// there (user manual of the portal, §5.1). A stay without adults notifies each minor. A guest without a birth date cannot be placed.</para>
/// <para><b>Mandatory</b> (official page): structure, last and first name, birth date (01.01.1900 to today), arrival and departure (from
/// 01.01.2000), nationality as ISO alpha-3, document type. A stay with a guest that misses any of them, or with fewer guests recorded than the
/// booking counts, is left out whole and reported per guest: the file never holds a row the portal would refuse.</para>
/// <para><b>Optional columns</b> (NF-126): «Numero bollettino» is the code of the booking on every row, «Numero autovettura» the plate of the stay on
/// the row of its head, «Indirizzo», «CAP», «Domicilio» and «Nazione» the home of the guest as the check-in has it. A guest without them is still a
/// row: they never keep a stay out of the file.</para>
/// <para><b>Groups and minors</b> (decision D11 ⚖, the counting is to confirm with the police): a stay whose guests are all typed head of group or
/// group member, with one head of group and <see cref="GroupMinSize"/> or more persons, is ONE row, the leader's, with every other member counted in
/// «Mag14» (14 or more, adults included) and «Min14» (under 14); the members are on the sheet of the group (<see cref="WriteGroupSheet"/>), kept five
/// years. A family is never a group. A stay with minors and no adult notifies each minor and warns that the parents' authorization is needed.</para>
/// </remarks>
public static class TicinoPoliceFile
{
    public const int GroupMinSize = 6;

    public static readonly IReadOnlyList<string> GroupSheetColumns = ["Nome", "Cognome", "Data di nascita", "Nazionalità"];

    public const string PortalUrl = "https://www.notifiche.ti.ch";

    public static readonly DateOnly MinBirthDate = new(1900, 1, 1);

    public static readonly DateOnly MinStayDate = new(2000, 1, 1);

    public const int AdultAge = 18;

    /// <summary>Age from which an accompanying minor is counted in «Mag14» rather than «Min14».</summary>
    public const int Over14Age = 14;

    public static readonly IReadOnlyList<string> CsvColumns =
    [
        "Id Struttura", "Cognome", "Nome", "Data di nascita", "Nazionalità", "Data di arrivo", "Data di partenza", "Luogo di nascita",
        "Numero bollettino", "Indirizzo", "CAP", "Domicilio", "Nazione", "Numero autovettura", "Mag14", "Min14", "Tipo documento",
        "Numero documento",
    ];

    public static TicinoDayPlan Plan(string structureId, IEnumerable<TicinoStayFacts> stays, DateOnly today)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structureId);
        ArgumentNullException.ThrowIfNull(stays);

        var registrations = new List<TicinoRegistration>();
        var issues = new List<TicinoGuestIssue>();
        var warnings = new List<TicinoStayWarning>();
        foreach (var stay in stays.OrderBy(s => s.Arrival).ThenBy(s => s.BookingId))
        {
            var (rows, stayIssues, stayWarnings) = PlanStay(structureId, stay, today);
            warnings.AddRange(stayWarnings);
            if (stayIssues.Count > 0)
                issues.AddRange(stayIssues);
            else
                registrations.AddRange(rows);
        }

        return new TicinoDayPlan(registrations, issues) { Warnings = warnings };
    }

    /// <summary>
    /// The position of the leader when the guests of a stay are a group of <see cref="GroupMinSize"/> or more persons (decision D11): every guest typed
    /// head of group or group member, and exactly one head of group. Null otherwise: a family, a smaller group or a stay that mixes kinds keeps the rule
    /// of the adults (each adult a row).
    /// </summary>
    public static int? GroupLeaderPosition(IReadOnlyCollection<(int Position, StayGuestType Type)> guests)
    {
        ArgumentNullException.ThrowIfNull(guests);
        if (guests.Count < GroupMinSize || guests.Any(g => g.Type is not (StayGuestType.HeadOfGroup or StayGuestType.GroupMember)))
            return null;

        var leaders = guests.Where(g => g.Type == StayGuestType.HeadOfGroup).Select(g => g.Position).ToList();
        return leaders.Count == 1 ? leaders[0] : null;
    }

    public static IReadOnlySet<int> NotifiedPositions(IEnumerable<(int Position, StayGuestType Type, DateOnly? BirthDate)> guests, DateOnly arrival)
    {
        ArgumentNullException.ThrowIfNull(guests);
        var all = guests.ToList();
        if (GroupLeaderPosition(all.Select(g => (g.Position, g.Type)).ToList()) is { } leader)
            return new HashSet<int> { leader };

        var aged = all
            .Where(g => g.BirthDate is not null)
            .Select(g => (g.Position, Age: AgeAt(g.BirthDate!.Value, arrival)))
            .ToList();
        var adults = aged.Where(g => g.Age >= AdultAge).Select(g => g.Position).ToHashSet();
        return adults.Count > 0 ? adults : aged.Select(g => g.Position).ToHashSet();
    }

    /// <summary>
    /// The positions of the <b>adults</b> among <see cref="NotifiedPositions"/> (18 or more at <paramref name="arrival"/>): the guests whose document the check-in stores because the file
    /// notifies them on their own row, family and group members included. Never a minor, not even in a stay of minors only, where the file notifies each minor; never a member of a group
    /// of six or more, whose leader is the only row. It is <see cref="NotifiedPositions"/> and nothing else that decides which guests these are: the host's edit dialog reads their numbers
    /// through <c>GET api/alloggiati/{bookingId}/stay-guests/document-numbers</c> (a single guest and a head have their document by their kind; the adult members of a stay of Ticino
    /// have it because the file notifies them).
    /// </summary>
    public static IReadOnlySet<int> NotifiedAdultPositions(IEnumerable<(int Position, StayGuestType Type, DateOnly? BirthDate)> guests, DateOnly arrival)
    {
        ArgumentNullException.ThrowIfNull(guests);
        var all = guests.ToList();
        var notified = NotifiedPositions(all, arrival);
        return all
            .Where(g => notified.Contains(g.Position) && g.BirthDate is { } born && AgeAt(born, arrival) >= AdultAge)
            .Select(g => g.Position)
            .ToHashSet();
    }

    public static IReadOnlyList<TicinoGroupMember>? GroupSheetOf(TicinoStayFacts stay)
    {
        ArgumentNullException.ThrowIfNull(stay);
        var guests = stay.Guests.OrderBy(g => g.Position).ToList();
        if (GroupLeaderPosition(guests.Select(g => (g.Position, g.Type)).ToList()) is not { } leader)
            return null;

        return guests
            .OrderBy(g => g.Position == leader ? 0 : 1)
            .ThenBy(g => g.Position)
            .Select(g => new TicinoGroupMember(
                Clean(g.FirstName),
                Clean(g.LastName),
                g.BirthDate,
                TicinoCountryCodes.FromCheckIn(g.CitizenshipCode, g.CitizenshipName) ?? string.Empty))
            .ToList();
    }

    private static (List<TicinoRegistration> Rows, List<TicinoGuestIssue> Issues, List<TicinoStayWarning> Warnings) PlanStay(
        string structureId,
        TicinoStayFacts stay,
        DateOnly today)
    {
        var rows = new List<TicinoRegistration>();
        var issues = new List<TicinoGuestIssue>();
        var warnings = new List<TicinoStayWarning>();
        var guests = stay.Guests.OrderBy(g => g.Position).ToList();
        if (guests.Count == 0 || guests.Count < stay.NumberOfGuests)
            issues.Add(new TicinoGuestIssue(stay.BookingId, null, stay.BookerName, [TicinoMissing.Guests]));

        var ages = guests.ToDictionary(g => g.Position, g => g.BirthDate is { } born ? AgeAt(born, stay.Arrival) : (int?)null);
        var adults = guests.Where(g => ages[g.Position] >= AdultAge).ToList();
        var minors = guests.Where(g => ages[g.Position] is { } age && age < AdultAge).ToList();

        if (adults.Count == 0 && minors.Count > 0)
            warnings.Add(new TicinoStayWarning(stay.BookingId, TicinoWarnings.ParentalAuthorizationNeeded));

        if (GroupLeaderPosition(guests.Select(g => (g.Position, g.Type)).ToList()) is { } leader)
        {
            var (groupRows, groupIssues, sheetComplete) = PlanGroup(structureId, stay, guests, ages, leader, today);
            if (!sheetComplete)
                warnings.Add(new TicinoStayWarning(stay.BookingId, TicinoWarnings.GroupSheetIncomplete));
            issues.AddRange(groupIssues);
            return (groupRows, issues, warnings);
        }

        var notified = NotifiedPositions(guests.Select(g => (g.Position, g.Type, g.BirthDate)), stay.Arrival);

        // The minors are counted on one adult: the head of family or group, otherwise the first adult. A stay of minors counts none.
        var head = adults.FirstOrDefault(g => g.Type is StayGuestType.HeadOfFamily or StayGuestType.HeadOfGroup) ?? adults.FirstOrDefault();
        var over14 = head is null ? 0 : minors.Count(g => ages[g.Position] >= Over14Age);
        var under14 = head is null ? 0 : minors.Count - over14;

        var carRow = head?.Position ?? guests.FirstOrDefault(g => notified.Contains(g.Position))?.Position;

        foreach (var guest in guests)
        {
            var name = $"{Clean(guest.FirstName)} {Clean(guest.LastName)}".Trim();
            var missing = new List<string>();
            if (ages[guest.Position] is null || guest.BirthDate < MinBirthDate || guest.BirthDate > today)
            {
                missing.Add(TicinoMissing.BirthDate);
            }

            var isNotified = notified.Contains(guest.Position) || ages[guest.Position] is null;
            var nationality = TicinoCountryCodes.FromCheckIn(guest.CitizenshipCode, guest.CitizenshipName);
            var document = DocumentKindOf(guest);
            if (isNotified)
            {
                if (Clean(guest.FirstName).Length == 0)
                    missing.Add(TicinoMissing.FirstName);
                if (Clean(guest.LastName).Length == 0)
                    missing.Add(TicinoMissing.LastName);
                if (nationality is null)
                    missing.Add(TicinoMissing.Nationality);
                if (document is null)
                    missing.Add(TicinoMissing.DocumentType);
            }

            if (missing.Count > 0)
            {
                issues.Add(new TicinoGuestIssue(stay.BookingId, guest.Position, name.Length > 0 ? name : stay.BookerName, missing));
                continue;
            }

            if (!isNotified)
                continue;

            var isHead = guest.Position == head?.Position;
            rows.Add(RowOf(structureId, stay, guest, isHead ? over14 : 0, isHead ? under14 : 0, withPlate: guest.Position == carRow));
        }

        return (rows, issues, warnings);
    }

    private static (List<TicinoRegistration> Rows, List<TicinoGuestIssue> Issues, bool SheetComplete) PlanGroup(
        string structureId,
        TicinoStayFacts stay,
        List<TicinoGuestFacts> guests,
        Dictionary<int, int?> ages,
        int leaderPosition,
        DateOnly today)
    {
        var issues = new List<TicinoGuestIssue>();
        var sheetComplete = true;
        foreach (var guest in guests)
        {
            var missing = new List<string>();
            if (ages[guest.Position] is null || guest.BirthDate < MinBirthDate || guest.BirthDate > today)
                missing.Add(TicinoMissing.BirthDate);

            var nationality = TicinoCountryCodes.FromCheckIn(guest.CitizenshipCode, guest.CitizenshipName);
            var named = Clean(guest.FirstName).Length > 0 && Clean(guest.LastName).Length > 0;
            if (guest.Position == leaderPosition)
            {
                if (Clean(guest.FirstName).Length == 0)
                    missing.Add(TicinoMissing.FirstName);
                if (Clean(guest.LastName).Length == 0)
                    missing.Add(TicinoMissing.LastName);
                if (nationality is null)
                    missing.Add(TicinoMissing.Nationality);
                if (DocumentKindOf(guest) is null)
                    missing.Add(TicinoMissing.DocumentType);
            }
            else if (!named || nationality is null)
            {
                sheetComplete = false;
            }

            if (missing.Count > 0)
            {
                var name = $"{Clean(guest.FirstName)} {Clean(guest.LastName)}".Trim();
                issues.Add(new TicinoGuestIssue(stay.BookingId, guest.Position, name.Length > 0 ? name : stay.BookerName, missing));
            }
        }

        if (issues.Count > 0)
            return ([], issues, sheetComplete);

        var leader = guests.First(g => g.Position == leaderPosition);
        var others = guests.Where(g => g.Position != leaderPosition).ToList();
        var over14 = others.Count(g => ages[g.Position] >= Over14Age);
        var row = RowOf(structureId, stay, leader, over14, others.Count - over14, withPlate: true) with
        {
            Kind = TicinoRowKind.Group,
            GroupSize = guests.Count,
        };
        return ([row], issues, sheetComplete);
    }

    private static TicinoRegistration RowOf(string structureId, TicinoStayFacts stay, TicinoGuestFacts guest, int over14, int under14, bool withPlate)
    {
        return new TicinoRegistration(
            stay.BookingId,
            guest.Position,
            structureId,
            Clean(guest.FirstName),
            Clean(guest.LastName),
            guest.BirthDate!.Value,
            TicinoCountryCodes.FromCheckIn(guest.CitizenshipCode, guest.CitizenshipName)!,
            stay.Arrival,
            Clean(guest.BornInItaly == true ? guest.BirthComuneName : guest.BirthCountryName),
            Clean(guest.Street),
            Clean(guest.PostalCode),
            Clean(DomicileOf(guest)),
            ResidenceNationOf(guest) ?? string.Empty,
            stay.Departure,
            over14,
            under14,
            DocumentKindOf(guest)!.Value,
            Clean(guest.DocumentNumber))
        {
            ReferenceNr = Clean(stay.ReferenceNumber),
            CarId = withPlate ? Clean(stay.VehiclePlate) : string.Empty,
        };
    }

    /// <summary>«Domicilio»: the comune of residence of a guest living in Italy when the check-in has it, otherwise the town the guest wrote.</summary>
    public static string? DomicileOf(TicinoGuestFacts guest) =>
        guest.ResidenceInItaly == true && !string.IsNullOrWhiteSpace(guest.ResidenceComuneName) ? guest.ResidenceComuneName : guest.City;

    public static int AgeAt(DateOnly born, DateOnly day)
    {
        var age = day.Year - born.Year;
        if (born > day.AddYears(-age))
            age--;
        return age;
    }

    /// <summary>
    /// The document type of the portal: from the Alloggiati code (passports <c>PAS*</c>, identity cards <c>IDENT</c>, <c>IDELE</c>,
    /// <c>CIDIP</c>, any other document «Other»), otherwise from the type chosen at the check-in; <c>null</c> when neither is known.
    /// </summary>
    public static TicinoDocumentKind? DocumentKindOf(TicinoGuestFacts guest)
    {
        ArgumentNullException.ThrowIfNull(guest);
        var code = guest.DocumentTypeCode?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(code))
        {
            if (code.StartsWith("PAS", StringComparison.Ordinal))
                return TicinoDocumentKind.Passport;
            return code is "IDENT" or "IDELE" or "CIDIP" ? TicinoDocumentKind.IdentityCard : TicinoDocumentKind.Other;
        }

        return guest.DocumentType switch
        {
            GuestDocumentType.Passport => TicinoDocumentKind.Passport,
            GuestDocumentType.IdentityCard => TicinoDocumentKind.IdentityCard,
            GuestDocumentType.DriversLicense or GuestDocumentType.Other => TicinoDocumentKind.Other,
            _ => null,
        };
    }

    /// <summary>«Nazione»: <c>ITA</c> for a guest living in Italy, otherwise the alpha-3 code of the state of residence the check-in has; <c>null</c> when it is not known.</summary>
    public static string? ResidenceNationOf(TicinoGuestFacts guest) =>
        guest.ResidenceInItaly == true ? "ITA" : TicinoCountryCodes.FromCheckIn(guest.ResidenceCountryCode, guest.ResidenceCountryName);

    public static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static byte[] WriteXml(IReadOnlyList<TicinoRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Any(r => !r.FitsXml))
            throw new ArgumentException("A row does not fit the XSD (postcode or country of residence).", nameof(registrations));

        var root = new XElement(
            "registrationForms",
            registrations.Select(r => new XElement(
                "registration",
                new XElement("userId", r.UserId),
                new XElement("firstname", r.FirstName),
                new XElement("lastname", r.LastName),
                new XElement("birthdate", IsoDate(r.BirthDate)),
                new XElement("nationality", r.Nationality),
                new XElement("arrivaldate", IsoDate(r.ArrivalDate)),
                new XElement("placeofbirth", r.PlaceOfBirth),
                new XElement("street", r.Street),
                new XElement("zip", r.Zip),
                new XElement("city", r.City),
                new XElement("nation", r.Nation),
                new XElement("departuredate", IsoDate(r.DepartureDate)),
                new XElement("persover14", r.PersonsOver14.ToString(CultureInfo.InvariantCulture)),
                new XElement("persunder14", r.PersonsUnder14.ToString(CultureInfo.InvariantCulture)),
                new XElement("documentType", XmlDocumentType(r.DocumentKind)),
                new XElement("documentNumber", r.DocumentNumber),
                new XElement("carid", r.CarId),
                new XElement("referenceNr", r.ReferenceNr))));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        return stream.ToArray();
    }

    public static byte[] WriteCsv(IReadOnlyList<TicinoRegistration> registrations, TicinoCsvCharset charset = TicinoCsvCharset.Windows1252) =>
        WriteCsvFile(registrations, charset).Content;

    /// <summary>
    /// The official CSV: the header of the model and one line per row, separated by semicolons, lines ended by CRLF (none after the last,
    /// as in the model), dates <c>dd.MM.yyyy</c>, document types in Italian, encoded in <paramref name="charset"/>: Windows-1252 like the model
    /// (its header is the model's byte for byte), or UTF-8 by configuration. A character the set cannot hold loses its accent (<c>Ł</c> becomes
    /// <c>L</c>, <c>ș</c> becomes <c>s</c>) or, when it has none to lose, becomes <c>?</c> (the last resort, never a broken byte), and
    /// <see cref="TicinoCsvFile.TransliteratedCount"/> says how many did; a semicolon inside a value becomes a comma.
    /// </summary>
    public static TicinoCsvFile WriteCsvFile(IReadOnlyList<TicinoRegistration> registrations, TicinoCsvCharset charset)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var lines = new List<string> { string.Join(';', CsvColumns) };
        lines.AddRange(registrations.Select(r => string.Join(';', new[]
        {
            r.UserId, r.LastName, r.FirstName, SwissDate(r.BirthDate), r.Nationality, SwissDate(r.ArrivalDate), SwissDate(r.DepartureDate),
            r.PlaceOfBirth, r.ReferenceNr, r.Street, r.Zip, r.City, r.Nation, r.CarId,
            r.PersonsOver14.ToString(CultureInfo.InvariantCulture), r.PersonsUnder14.ToString(CultureInfo.InvariantCulture),
            CsvDocumentType(r.DocumentKind), r.DocumentNumber,
        }.Select(CsvValue))));
        var text = Transliterate(string.Join("\r\n", lines), charset, out var transliterated);
        return new TicinoCsvFile(EncodingOf(charset).GetBytes(text), CharsetName(charset), transliterated);
    }

    public static string CharsetName(TicinoCsvCharset charset) => charset == TicinoCsvCharset.Utf8 ? "utf-8" : "windows-1252";

    /// <summary>
    /// The sheet of a group (decision D11): the first name, the last name, the birth date and the nationality (ISO alpha-3) of every member, kept five
    /// years at the disposal of the police and of the regional tourism organizations. It is a file the host opens in a spreadsheet, so the one writer of
    /// those files writes it (<see cref="CsvWriter"/>): UTF-8 with its byte order mark (a spreadsheet then reads the accents), semicolons, the header of
    /// <see cref="GroupSheetColumns"/>, every line ended by CRLF, dates <c>dd.MM.yyyy</c>; a datum that is not known stays empty. The names are typed by
    /// the guests: a text that a spreadsheet would run as a formula (it starts with <c>= + - @</c>, a tab or a CR) gets a leading apostrophe and is never
    /// calculated, and a cell with a semicolon, a quote or a line break is quoted. Never stored by CasaZen: the host keeps it, or files it in the archive
    /// of the receipts (type <c>GroupSheet</c>).
    /// </summary>
    public static byte[] WriteGroupSheet(IReadOnlyList<TicinoGroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var rows = new List<IReadOnlyList<object?>>();
        rows.Add([.. GroupSheetColumns]);
        foreach (var member in members)
            rows.Add([member.FirstName, member.LastName, member.BirthDate is { } born ? SwissDate(born) : string.Empty, member.Nationality]);

        return CsvWriter.Write(rows);
    }

    public static string XmlDocumentType(TicinoDocumentKind kind) => kind switch
    {
        TicinoDocumentKind.Passport => "Passport",
        TicinoDocumentKind.IdentityCard => "Identity card",
        _ => "Other",
    };

    public static string CsvDocumentType(TicinoDocumentKind kind) => kind switch
    {
        TicinoDocumentKind.Passport => "Passaporto",
        TicinoDocumentKind.IdentityCard => "Carta identità",
        _ => "Altro",
    };

    private static string IsoDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string SwissDate(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string CsvValue(string value) => value.Replace(';', ',').Replace('"', '\'');

    private static string Transliterate(string text, TicinoCsvCharset charset, out int transliterated)
    {
        transliterated = 0;
        text = Composed(text);
        var builder = new StringBuilder(text.Length);
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var consumed);
            rest = rest[consumed..];
            if (status == OperationStatus.Done && CanEncode(rune, charset))
            {
                builder.Append(rune.ToString());
                continue;
            }

            transliterated++;
            var plain = status == OperationStatus.Done ? WithoutAccent(rune) : string.Empty;
            builder.Append(plain.Length > 0 && plain.EnumerateRunes().All(r => CanEncode(r, charset)) ? plain : "?");
        }

        return builder.ToString();
    }

    private static string Composed(string text)
    {
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out var consumed) != OperationStatus.Done)
                return text;

            rest = rest[consumed..];
        }

        return text.Normalize(NormalizationForm.FormC);
    }

    private static string WithoutAccent(Rune rune) => rune.Value switch
    {
        'Ł' => "L",
        'ł' => "l",
        'Đ' => "D",
        'đ' => "d",
        'Œ' => "OE",
        'œ' => "oe",
        'ı' => "i",
        'Ħ' => "H",
        'ħ' => "h",
        _ => new string(rune.ToString().Normalize(NormalizationForm.FormD)
            .Where(d => CharUnicodeInfo.GetUnicodeCategory(d) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
            .ToArray()),
    };

    private static bool CanEncode(Rune rune, TicinoCsvCharset charset) =>
        charset == TicinoCsvCharset.Utf8 || (rune.IsBmp && Windows1252Characters.Contains((char)rune.Value));

    private static Encoding EncodingOf(TicinoCsvCharset charset) =>
        charset == TicinoCsvCharset.Utf8 ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) : Windows1252.Value;

    private static readonly Lazy<Encoding> Windows1252 = new(() => CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? throw new InvalidOperationException("The Windows-1252 code page is not available."));

    private static readonly HashSet<char> Windows1252Characters = BuildWindows1252Characters();

    private static HashSet<char> BuildWindows1252Characters()
    {
        var strict = CodePagesEncodingProvider.Instance.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? throw new InvalidOperationException("The Windows-1252 code page is not available.");
        var characters = new HashSet<char>();
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            try
            {
                var decoded = strict.GetString([(byte)value]);
                if (decoded.Length == 1 && strict.GetBytes(decoded) is [var back] && back == value)
                    characters.Add(decoded[0]);
            }
            catch (DecoderFallbackException)
            {
            }
            catch (EncoderFallbackException)
            {
            }
        }

        return characters;
    }
}
