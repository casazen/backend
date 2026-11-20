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
    /// <summary>The official CSV model (<c>D-Modello_2.CSV</c>): semicolons, ISO-8859-1, dates <c>dd.MM.yyyy</c>.</summary>
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
public sealed record TicinoStayFacts(
    Guid BookingId,
    string BookerName,
    DateOnly Arrival,
    DateOnly Departure,
    int NumberOfGuests,
    IReadOnlyList<TicinoGuestFacts> Guests);

/// <summary>One row of the file: one notified person (an adult, or a minor travelling without an adult).</summary>
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
    /// <summary>Car plate: CasaZen does not ask for it.</summary>
    public string CarId => string.Empty;

    /// <summary>«Numero bollettino»: CasaZen has none.</summary>
    public string ReferenceNr => string.Empty;

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
/// </remarks>
public static class TicinoPoliceFile
{
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
        foreach (var stay in stays.OrderBy(s => s.Arrival).ThenBy(s => s.BookingId))
        {
            var (rows, stayIssues) = PlanStay(structureId, stay, today);
            if (stayIssues.Count > 0)
                issues.AddRange(stayIssues);
            else
                registrations.AddRange(rows);
        }

        return new TicinoDayPlan(registrations, issues);
    }

    private static (List<TicinoRegistration> Rows, List<TicinoGuestIssue> Issues) PlanStay(string structureId, TicinoStayFacts stay, DateOnly today)
    {
        var rows = new List<TicinoRegistration>();
        var issues = new List<TicinoGuestIssue>();
        var guests = stay.Guests.OrderBy(g => g.Position).ToList();
        if (guests.Count == 0 || guests.Count < stay.NumberOfGuests)
            issues.Add(new TicinoGuestIssue(stay.BookingId, null, stay.BookerName, [TicinoMissing.Guests]));

        var ages = guests.ToDictionary(g => g.Position, g => g.BirthDate is { } born ? AgeAt(born, stay.Arrival) : (int?)null);
        var adults = guests.Where(g => ages[g.Position] >= AdultAge).ToList();
        var minors = guests.Where(g => ages[g.Position] is { } age && age < AdultAge).ToList();
        var notified = adults.Count > 0 ? adults : minors;
        var head = adults.Count > 0
            ? adults.FirstOrDefault(g => g.Type is StayGuestType.HeadOfFamily or StayGuestType.HeadOfGroup) ?? adults[0]
            : null;
        var over14 = adults.Count > 0 ? minors.Count(g => ages[g.Position] >= Over14Age) : 0;
        var under14 = adults.Count > 0 ? minors.Count - over14 : 0;

        foreach (var guest in guests)
        {
            var name = $"{Clean(guest.FirstName)} {Clean(guest.LastName)}".Trim();
            var missing = new List<string>();
            if (ages[guest.Position] is null || guest.BirthDate < MinBirthDate || guest.BirthDate > today)
            {
                // Without a valid birth date a guest can be neither notified nor counted as a minor.
                missing.Add(TicinoMissing.BirthDate);
            }

            var isNotified = notified.Contains(guest) || ages[guest.Position] is null;
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

            var isHead = ReferenceEquals(guest, head);
            rows.Add(new TicinoRegistration(
                stay.BookingId,
                guest.Position,
                structureId,
                Clean(guest.FirstName),
                Clean(guest.LastName),
                guest.BirthDate!.Value,
                nationality!,
                stay.Arrival,
                Clean(guest.BornInItaly == true ? guest.BirthComuneName : guest.BirthCountryName),
                Clean(guest.Street),
                Clean(guest.PostalCode),
                Clean(guest.ResidenceInItaly == true ? guest.ResidenceComuneName : guest.City),
                ResidenceNationOf(guest) ?? string.Empty,
                stay.Departure,
                isHead ? over14 : 0,
                isHead ? under14 : 0,
                document!.Value,
                Clean(guest.DocumentNumber)));
        }

        return (rows, issues);
    }

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

    /// <summary>
    /// The official CSV: the header of the model and one line per row, separated by semicolons, lines ended by CRLF (none after the last,
    /// as in the model), dates <c>dd.MM.yyyy</c>, document types in Italian, encoded ISO-8859-1 like the model. A character outside that set
    /// loses its accent (<c>Ł</c> becomes <c>L</c>) or, when it has none to lose, becomes <c>?</c>; a semicolon inside a value becomes a comma.
    /// </summary>
    public static byte[] WriteCsv(IReadOnlyList<TicinoRegistration> registrations)
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
        return Encoding.Latin1.GetBytes(ToLatin1(string.Join("\r\n", lines)));
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

    /// <summary>The text with every character ISO-8859-1 can hold, the others without their accent or as <c>?</c>.</summary>
    private static string ToLatin1(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c <= 'ÿ')
            {
                builder.Append(c);
                continue;
            }

            var plain = c switch
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
                _ => new string(c.ToString().Normalize(NormalizationForm.FormD)
                    .Where(d => CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark).ToArray()),
            };
            builder.Append(plain.Length > 0 && plain.All(p => p <= 'ÿ') ? plain : "?");
        }

        return builder.ToString();
    }
}
