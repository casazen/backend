using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// The identifier of a structure on the Ticino police portal (casazen/backend#979): the «Numero identificativo LEAR / NO-LEAR» Ticino
/// Turismo gives a structure when it registers (examples of the official format page: <c>XNA9999</c>, <c>NL-99999999</c>). It is the first
/// field of every row of the file (<c>userId</c> / «Id Struttura»); the portal refuses a file whose identifier it does not know.
/// </summary>
public static partial class TicinoStructureId
{
    /// <summary>Longest identifier stored.</summary>
    public const int MaxLength = 20;

    /// <summary>Shortest identifier accepted.</summary>
    public const int MinLength = 3;

    /// <summary>Letters and digits, groups joined by single hyphens (after <see cref="Normalize"/>).</summary>
    public const string Pattern = "^[A-Z0-9]+(-[A-Z0-9]+)*$";

    /// <summary>The SharedResources key of the validation message.</summary>
    public const string InvalidFormatMessageKey = "TicinoStructureIdInvalid";

    /// <summary>Trimmed, without spaces, uppercase; <c>null</c> when blank.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return compact.Length == 0 ? null : compact;
    }

    /// <summary>True when the normalized value is 3 to 20 letters, digits and single hyphens.</summary>
    public static bool IsValid(string? value) =>
        Normalize(value) is { Length: >= MinLength and <= MaxLength } normalized && PatternRegex().IsMatch(normalized);

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex PatternRegex();
}

/// <summary>The formats of the file the portal accepts that CasaZen writes (the third, Excel, is not written).</summary>
public enum TicinoPoliceFileFormat
{
    /// <summary>The official CSV model (<c>D-Modello_2.CSV</c>): semicolons, Windows-1252 (or UTF-8 by configuration), dates <c>dd.MM.yyyy</c>.</summary>
    Csv = 1,

    /// <summary>The official XML (<c>F-Modello_4.xsd</c>): UTF-8, dates <c>yyyy-MM-dd</c>.</summary>
    Xml = 2,
}

/// <summary>The three document types of the portal.</summary>
public enum TicinoDocumentKind
{
    Passport = 1,
    IdentityCard = 2,
    Other = 3,
}

/// <summary>The character sets the CSV is written in (<c>GuestNotifications:TicinoCsvEncoding</c>, decision D8).</summary>
public enum TicinoCsvCharset
{
    /// <summary>Windows-1252, the single-byte set of the official model (the default): its header is written byte for byte as the model's.</summary>
    Windows1252 = 1,

    /// <summary>UTF-8 without byte order mark (the mark would become part of the name of the first column).</summary>
    Utf8 = 2,
}

/// <summary>What a row of the file stands for (the day response says <c>kind</c>: <c>guest</c> or <c>group</c>).</summary>
public enum TicinoRowKind
{
    /// <summary>One notified person: an adult (with the minors of the stay counted on them, or none), or a minor of a stay without adults.</summary>
    Guest = 1,

    /// <summary>
    /// The leader of a group of <see cref="TicinoPoliceFile.GroupMinSize"/> or more persons, every other member counted on the row (decision D11): the
    /// other members are not rows, they are on the sheet of the group.
    /// </summary>
    Group = 2,
}

/// <summary>A person on the sheet of a group (decision D11): what the sheet keeps of every member, the leader first.</summary>
/// <param name="FirstName">The first name; empty when the check-in has none.</param>
/// <param name="LastName">The last name; empty when the check-in has none.</param>
/// <param name="BirthDate">The birth date; null when the check-in has none.</param>
/// <param name="Nationality">ISO 3166-1 alpha-3; empty when it is not known.</param>
public sealed record TicinoGroupMember(string FirstName, string LastName, DateOnly? BirthDate, string Nationality);

/// <summary>Something about a stay the file alone does not settle (the day lists it with the booking); the codes of <see cref="TicinoWarnings"/>.</summary>
public sealed record TicinoStayWarning(Guid BookingId, string Code);

/// <summary>The stable codes of <see cref="TicinoStayWarning"/> (the clients translate them).</summary>
public static class TicinoWarnings
{
    /// <summary>
    /// A stay with minors and no adult (decision D11): each minor is a row, and the written authorization of the parents is needed, kept five years
    /// (receipt type <c>ParentalAuthorization</c>); without it the police is to be told at once.
    /// </summary>
    public const string ParentalAuthorizationNeeded = "parental_authorization_needed";

    /// <summary>
    /// A group of six or more whose sheet misses the name or the nationality of a member: the leader's row can go, the sheet that is kept with it is not
    /// complete until the guests are.
    /// </summary>
    public const string GroupSheetIncomplete = "group_sheet_incomplete";
}

/// <summary>A CSV for the portal (<see cref="TicinoPoliceFile.WriteCsvFile"/>).</summary>
/// <param name="Content">The bytes, in <paramref name="Charset"/>.</param>
/// <param name="Charset">The IANA name of the character set: <c>windows-1252</c> or <c>utf-8</c> (the <c>charset</c> of the content type).</param>
/// <param name="TransliteratedCount">The characters of the rows the character set cannot hold, written without their accent or as <c>?</c>.</param>
public sealed record TicinoCsvFile(byte[] Content, string Charset, int TransliteratedCount);

/// <summary>One guest of a stay as the check-in has it (a <see cref="StayGuest"/>, with the address of its guest record when linked).</summary>
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
    string? City);

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
    string? VehiclePlate = null);

/// <summary>One row of the file: one notified person (an adult, or a minor travelling without an adult), or the leader of a group of six or more.</summary>
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

    /// <summary>«Numero bollettino» / <c>referenceNr</c>: the code of the booking (<c>XXXXX-XXXXX</c>), so the host finds the row again on the portal.</summary>
    public string ReferenceNr { get; init; } = string.Empty;

    /// <summary><see cref="TicinoRowKind.Group"/> on the row of the leader of a group of six or more, whose counters hold every other member.</summary>
    public TicinoRowKind Kind { get; init; } = TicinoRowKind.Guest;

    /// <summary>The persons of the group, the leader included, on a group row; 0 on the row of a guest.</summary>
    public int GroupSize { get; init; }

    /// <summary>The row fits the official XSD: the postcode is a whole number (<c>xs:integer</c>) and the country of residence is known.</summary>
    public bool FitsXml => Zip.Length is > 0 and <= 18 && Zip.All(char.IsAsciiDigit) && Nation.Length == 3;
}

/// <summary>
/// Why a guest (or a whole stay, <see cref="Position"/> <c>null</c>) cannot be in the file: the codes of <see cref="TicinoMissing"/>.
/// The stay is left out of the file until its data are complete.
/// </summary>
public sealed record TicinoGuestIssue(Guid BookingId, int? Position, string GuestName, IReadOnlyList<string> Missing);

/// <summary>The stable codes of what a guest misses for the file (the clients translate them).</summary>
public static class TicinoMissing
{
    public const string FirstName = "firstName";
    public const string LastName = "lastName";
    public const string BirthDate = "birthDate";
    public const string Nationality = "nationality";
    public const string DocumentType = "documentType";

    /// <summary>Stay level: the booking counts more guests than the check-in recorded.</summary>
    public const string Guests = "guests";
}

/// <summary>What the file of one arrival day holds: the rows of the stays whose data are complete, and what the others miss.</summary>
public sealed record TicinoDayPlan(IReadOnlyList<TicinoRegistration> Registrations, IReadOnlyList<TicinoGuestIssue> Issues)
{
    /// <summary>
    /// What the file alone does not settle, per stay, whether the stay is in the file or not: a stay of minors without adults needs the parents'
    /// authorization, a group sheet misses a name or a nationality (<see cref="TicinoWarnings"/>).
    /// </summary>
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
    /// <summary>Persons, the leader included, from which a group is notified as one row (decision D11; the rule of the canton: «almeno 6 persone»).</summary>
    public const int GroupMinSize = 6;

    /// <summary>The header of the sheet of a group (decision D11): the four data the rule asks of every member.</summary>
    public static readonly IReadOnlyList<string> GroupSheetColumns = ["Nome", "Cognome", "Data di nascita", "Nazionalità"];

    /// <summary>The portal where the host uploads the file.</summary>
    public const string PortalUrl = "https://www.notifiche.ti.ch";

    /// <summary>The first day of birth the portal accepts.</summary>
    public static readonly DateOnly MinBirthDate = new(1900, 1, 1);

    /// <summary>The first day of arrival or departure the portal accepts.</summary>
    public static readonly DateOnly MinStayDate = new(2000, 1, 1);

    /// <summary>Age from which a guest is notified on their own row.</summary>
    public const int AdultAge = 18;

    /// <summary>Age from which an accompanying minor is counted in «Mag14» rather than «Min14».</summary>
    public const int Over14Age = 14;

    /// <summary>The header of the official CSV model, column by column.</summary>
    public static readonly IReadOnlyList<string> CsvColumns =
    [
        "Id Struttura", "Cognome", "Nome", "Data di nascita", "Nazionalità", "Data di arrivo", "Data di partenza", "Luogo di nascita",
        "Numero bollettino", "Indirizzo", "CAP", "Domicilio", "Nazione", "Numero autovettura", "Mag14", "Min14", "Tipo documento",
        "Numero documento",
    ];

    /// <summary>
    /// The rows of the stays arriving on one day for the structure <paramref name="structureId"/>, and what the incomplete stays miss.
    /// <paramref name="today"/> is the last birth date accepted.
    /// </summary>
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

    /// <summary>
    /// The positions of the guests the file notifies on their own row, so the check-in asks the document of each (NF-132): every adult (18 or more at
    /// <paramref name="arrival"/>, family and group members included), or every minor when the stay has no adult; in a group of six or more only its
    /// leader (<see cref="GroupLeaderPosition"/>: the other members are counted on the leader's row). A guest without a birth date is not in it: nobody
    /// can tell yet whether they are a row.
    /// </summary>
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
    /// The members of the group of a stay for its sheet (decision D11), in the order of the check-in (the head of group first when the check-in put it
    /// first); null when the stay is not a group of <see cref="GroupMinSize"/> or more. A datum the check-in does not have stays empty.
    /// </summary>
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

        // Minors without an adult (D11): each minor is notified, and the written authorization of their parents is needed (kept five years).
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

        // The plate of the stay is written once: on the row of its head, or of its first notified minor when it has no adult.
        var carRow = head?.Position ?? guests.FirstOrDefault(g => notified.Contains(g.Position))?.Position;

        foreach (var guest in guests)
        {
            var name = $"{Clean(guest.FirstName)} {Clean(guest.LastName)}".Trim();
            var missing = new List<string>();
            if (ages[guest.Position] is null || guest.BirthDate < MinBirthDate || guest.BirthDate > today)
            {
                // Without a valid birth date a guest can be neither notified nor counted as a minor.
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

    /// <summary>
    /// A group of six or more (decision D11): ONE row, the leader's, with every other member counted in «Mag14» (14 or more, adults included) and «Min14»
    /// (under 14), and the plate of the stay. The leader needs what every row needs; the other members a valid birth date, without which they cannot be
    /// counted. A member without a name or a nationality does not hold the notification back (the member is not in the file), but the sheet kept with it
    /// is then incomplete (<c>SheetComplete</c> false).
    /// </summary>
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

    /// <summary>The row of a notified guest whose mandatory data are complete (the callers checked them).</summary>
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
    private static string? DomicileOf(TicinoGuestFacts guest) =>
        guest.ResidenceInItaly == true && !string.IsNullOrWhiteSpace(guest.ResidenceComuneName) ? guest.ResidenceComuneName : guest.City;

    /// <summary>Full years at <paramref name="day"/>.</summary>
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

    private static string? ResidenceNationOf(TicinoGuestFacts guest) =>
        guest.ResidenceInItaly == true ? "ITA" : TicinoCountryCodes.FromCheckIn(guest.ResidenceCountryCode, guest.ResidenceCountryName);

    /// <summary>Trimmed, inner runs of white space (line breaks included) as one space; empty when <c>null</c>.</summary>
    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ─── Writers ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The official XML: <c>&lt;registrationForms&gt;</c> with one <c>&lt;registration&gt;</c> per row, all 18 elements in the order of the XSD,
    /// UTF-8. Only for rows that <see cref="TicinoRegistration.FitsXml"/> (the caller checks <see cref="TicinoDayPlan.XmlAvailable"/>).
    /// </summary>
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

    /// <summary>The CSV in <paramref name="charset"/> (Windows-1252 by default), its bytes only: see <see cref="WriteCsvFile"/>.</summary>
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

    /// <summary>The IANA name of a character set, as the <c>charset</c> of a content type: <c>windows-1252</c> or <c>utf-8</c>.</summary>
    public static string CharsetName(TicinoCsvCharset charset) => charset == TicinoCsvCharset.Utf8 ? "utf-8" : "windows-1252";

    /// <summary>
    /// The sheet of a group (decision D11): the first name, the last name, the birth date and the nationality (ISO alpha-3) of every member, kept five
    /// years at the disposal of the police and of the regional tourism organizations. CSV in UTF-8 with its byte order mark (a spreadsheet then reads
    /// the accents), semicolons, the header of <see cref="GroupSheetColumns"/>, lines ended by CRLF, dates <c>dd.MM.yyyy</c>; a datum that is not known
    /// stays empty, a semicolon inside a value becomes a comma. Never stored by CasaZen: the host keeps it, or files it in the archive of the receipts
    /// (type <c>GroupSheet</c>).
    /// </summary>
    public static byte[] WriteGroupSheet(IReadOnlyList<TicinoGroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var lines = new List<string> { string.Join(';', GroupSheetColumns) };
        lines.AddRange(members.Select(m => string.Join(';', new[]
        {
            m.FirstName, m.LastName, m.BirthDate is { } born ? SwissDate(born) : string.Empty, m.Nationality,
        }.Select(CsvValue))));
        return [.. Encoding.UTF8.GetPreamble(), .. new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(string.Join("\r\n", lines))];
    }

    /// <summary>The value of <c>documentType</c> in the XML.</summary>
    public static string XmlDocumentType(TicinoDocumentKind kind) => kind switch
    {
        TicinoDocumentKind.Passport => "Passport",
        TicinoDocumentKind.IdentityCard => "Identity card",
        _ => "Other",
    };

    /// <summary>The value of «Tipo documento» in the CSV.</summary>
    public static string CsvDocumentType(TicinoDocumentKind kind) => kind switch
    {
        TicinoDocumentKind.Passport => "Passaporto",
        TicinoDocumentKind.IdentityCard => "Carta identità",
        _ => "Altro",
    };

    private static string IsoDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string SwissDate(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string CsvValue(string value) => value.Replace(';', ',').Replace('"', '\'');

    /// <summary>
    /// The text with every character <paramref name="charset"/> can hold kept as it is, and the others without their accent or, as the last resort,
    /// as <c>?</c>; <paramref name="transliterated"/> counts the characters that were changed (a character outside the basic plane counts once).
    /// </summary>
    private static string Transliterate(string text, TicinoCsvCharset charset, out int transliterated)
    {
        transliterated = 0;
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

            // A broken surrogate (never a character) or a character the set cannot hold.
            transliterated++;
            var plain = status == OperationStatus.Done ? WithoutAccent(rune) : string.Empty;
            builder.Append(plain.Length > 0 && plain.EnumerateRunes().All(r => CanEncode(r, charset)) ? plain : "?");
        }

        return builder.ToString();
    }

    /// <summary>The letter without its accent (<c>Ł</c> → <c>L</c>, <c>ș</c> → <c>s</c>), or empty when it has none to lose.</summary>
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

    /// <summary>Windows-1252 from the code pages of .NET (the same provider the bank statement reader uses).</summary>
    private static readonly Lazy<Encoding> Windows1252 = new(() => CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? throw new InvalidOperationException("The Windows-1252 code page is not available."));

    /// <summary>Every character Windows-1252 holds: the ones its 256 bytes decode to and encode back to the same byte.</summary>
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
                // A byte Windows-1252 leaves undefined.
            }
            catch (EncoderFallbackException)
            {
                // A character that does not go back to the same byte.
            }
        }

        return characters;
    }
}
