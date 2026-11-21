using System.Globalization;
using Casazen.Core.Utilities;

namespace Casazen.Core.Regulatory.GuestRegistration;

/// <summary>
/// One line of the guest register of a Swiss property (E31/F01/US01/T01, NF-130, decision D12b): one guest of a stay that arrived in the period, an adult or a minor. What the check-in
/// does not have stays empty (<c>null</c>, or an empty text): the register lists every guest of the stay and never leaves one out for a missing value.
/// </summary>
/// <param name="Arrival">The arrival day of the stay.</param>
/// <param name="Departure">The departure day of the stay.</param>
/// <param name="LastName">The last name, white space tidied; empty when the check-in has none.</param>
/// <param name="FirstName">The first name, white space tidied; empty when the check-in has none.</param>
/// <param name="BirthDate">The date of birth; <c>null</c> when the check-in has none.</param>
/// <param name="Nationality">ISO 3166-1 alpha-3 through <see cref="TicinoCountryCodes"/>; empty when the nationality is not in the table (never guessed).</param>
/// <param name="DocumentKind">The kind of the document of the guest (<see cref="TicinoPoliceFile.DocumentKindOf"/>); <c>null</c> when it is not known.</param>
/// <param name="DocumentNumber">The number of the document; empty for a guest the check-in asked none (a family or group member, a minor).</param>
/// <param name="Street">Street and number of the home of the guest, as written.</param>
/// <param name="PostalCode">Postal code of the home of the guest, as written.</param>
/// <param name="City">The town of the home of the guest (<see cref="TicinoPoliceFile.DomicileOf"/>).</param>
/// <param name="Country">ISO 3166-1 alpha-3 of the state of residence (<see cref="TicinoPoliceFile.ResidenceNationOf"/>); empty when it is not known.</param>
/// <param name="VehiclePlate">The plate of the vehicle of the stay, repeated on every line of the stay so that each line stands on its own.</param>
/// <param name="BookingCode">The code of the booking as hosts read it (<c>XXXXX-XXXXX</c>), the same on every line of the stay.</param>
/// <param name="Minor"><c>true</c> under 18 at the arrival, <c>false</c> from 18; <c>null</c> when the check-in has no date of birth (or one after the arrival).</param>
public sealed record GuestRegisterRow(
    DateOnly Arrival,
    DateOnly Departure,
    string LastName,
    string FirstName,
    DateOnly? BirthDate,
    string Nationality,
    TicinoDocumentKind? DocumentKind,
    string DocumentNumber,
    string Street,
    string PostalCode,
    string City,
    string Country,
    string VehiclePlate,
    string BookingCode,
    bool? Minor);

/// <summary>
/// The guest register of a Swiss property for a period, as a clean table (E31/F01/US01/T01, NF-130, decision D12b of <c>alloggiati-ch/analisi-tecnica.md</c>): the host's own record for
/// the cantons that only ask for a register to be kept and shown to the police, and a source to copy from for the cantons that notify on a portal. CasaZen writes a table and gives no
/// legal advice: how long the host keeps it is the hint of the regime (<see cref="GuestRegistrationRegime.RetentionYears"/>), not a rule of this file.
/// </summary>
/// <remarks>
/// <para><b>The columns</b> (<see cref="HeaderKeys"/>, in this order): arrival, departure, last name, first name, date of birth, nationality (alpha-3), document type, document number,
/// street, postal code, city, country (alpha-3), vehicle plate, booking code, minor (yes or no) and an <b>empty</b> signature column for the printed copy. One line for each guest of each stay
/// that arrived in the period: the adults and the minors, the stay's own check-in rows or, when it has none, the booker (<see cref="TicinoStayFacts.From"/>, the reading the Ticino file
/// uses).</para>
/// <para><b>The file</b> is written by the one writer of the files the host opens in a spreadsheet (<see cref="CsvWriter"/>, SR-10): UTF-8 with its byte order mark, <c>;</c> between the cells,
/// one header line, every line ended by CRLF. <b>No comment, title or note line</b>: the file stays a table a spreadsheet, a sort or a print can use as it is. A text that a spreadsheet would
/// run as a formula (it starts with <c>= + - @</c>, a tab or a CR) is written with a leading apostrophe and never calculated; a cell with a semicolon, a quote or a line break is quoted.
/// A register of a period with no guest is the header line alone: an answer, not an error. The dates are <c>dd.MM.yyyy</c>, as in the Ticino file and in the group sheet.</para>
/// <para><b>The words</b> (the header, yes, the three kinds of document) are resource keys of <c>SharedResources</c>: the caller reads them in the language of the request
/// (<see cref="Casazen.Core.Documents.IDocumentTexts"/>), so no text to translate is written in the code. The one word that reads the same in Italian and in English, «no»
/// (<see cref="NoWord"/>), is a constant of the code and not a key: the localization test of the resource files refuses a key whose English text is the Italian one. The nationality and
/// the country are codes, never words.</para>
/// <para><b>Never stored.</b> The file is rebuilt from the check-in on every request; the service that serves it writes one audit row with no personal data.</para>
/// </remarks>
public static class GuestRegisterFile
{
    /// <summary>The longest period of a register, both ends counted: a year, a leap year included.</summary>
    public const int MaxPeriodDays = 366;

    /// <summary>The code of a period whose end is before its start, or has no day after it (400).</summary>
    public const string PeriodInvalidCode = "swiss_period_invalid";

    /// <summary>The code of a period longer than <see cref="MaxPeriodDays"/> days (400).</summary>
    public const string PeriodTooLongCode = "swiss_period_too_long";

    /// <summary>The content type of the file: UTF-8, with its byte order mark.</summary>
    public const string ContentType = "text/csv; charset=utf-8";

    /// <summary>The resource key of the word «yes» of the column «minor».</summary>
    public const string YesKey = "GuestRegisterYes";

    /// <summary>
    /// The word «no» of the column «minor»: it reads the same in Italian and in English, so it is not a text to translate and is not in the resource files (the localization test of
    /// the resource files wants a different English text for every key).
    /// </summary>
    public const string NoWord = "No";

    /// <summary>The resource key of the word «passport» of the column «document type».</summary>
    public const string PassportKey = "GuestRegisterDocumentPassport";

    /// <summary>The resource key of the words «identity card» of the column «document type».</summary>
    public const string IdentityCardKey = "GuestRegisterDocumentIdentityCard";

    /// <summary>The resource key of the word «other» of the column «document type»: any document that is neither a passport nor an identity card.</summary>
    public const string OtherDocumentKey = "GuestRegisterDocumentOther";

    /// <summary>The resource keys of the header, column by column; the last one is the signature column, whose cells are empty.</summary>
    public static readonly IReadOnlyList<string> HeaderKeys =
    [
        "GuestRegisterColArrival",
        "GuestRegisterColDeparture",
        "GuestRegisterColLastName",
        "GuestRegisterColFirstName",
        "GuestRegisterColBirthDate",
        "GuestRegisterColNationality",
        "GuestRegisterColDocumentType",
        "GuestRegisterColDocumentNumber",
        "GuestRegisterColStreet",
        "GuestRegisterColPostalCode",
        "GuestRegisterColCity",
        "GuestRegisterColCountry",
        "GuestRegisterColVehiclePlate",
        "GuestRegisterColBookingCode",
        "GuestRegisterColMinor",
        "GuestRegisterColSignature",
    ];

    /// <summary>Every resource key the file reads (for the tests that look for it in the two languages).</summary>
    public static IReadOnlyList<string> AllKeys { get; } = [.. HeaderKeys, YesKey, PassportKey, IdentityCardKey, OtherDocumentKey];

    /// <summary>
    /// What is wrong with a period, as the code of the error, or <c>null</c> when the register can be made: <see cref="PeriodInvalidCode"/> when the end is before the start (or is the
    /// last day the calendar has), <see cref="PeriodTooLongCode"/> when it is longer than <see cref="MaxPeriodDays"/> days, both ends counted.
    /// </summary>
    public static string? PeriodErrorOf(DateOnly from, DateOnly to)
    {
        if (to < from || to == DateOnly.MaxValue)
            return PeriodInvalidCode;

        return to.DayNumber - from.DayNumber >= MaxPeriodDays ? PeriodTooLongCode : null;
    }

    /// <summary>
    /// The lines of one stay: one for each guest of its check-in, in their order (the booker alone when the check-in has none). Nobody is left out for a datum the check-in does not
    /// have: the cell stays empty.
    /// </summary>
    public static IReadOnlyList<GuestRegisterRow> RowsOf(TicinoStayFacts stay)
    {
        ArgumentNullException.ThrowIfNull(stay);

        return stay.Guests
            .OrderBy(g => g.Position)
            .Select(g => RowOf(stay, g))
            .ToList();
    }

    /// <summary>
    /// The file: the header, then the lines as they come (the caller orders them). <paramref name="text"/> says a resource key in the language of the reader. At most
    /// <see cref="CsvWriter.MaxRows"/> lines, the header included (422 <c>csv_too_many_rows</c>).
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">There are more lines than the writer takes (422 <c>csv_too_many_rows</c>).</exception>
    public static byte[] Write(IReadOnlyList<GuestRegisterRow> rows, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<IReadOnlyList<object?>>();
        lines.Add([.. HeaderKeys.Select(key => (object?)text(key))]);
        foreach (var row in rows)
            lines.Add(CellsOf(row, text));

        return CsvWriter.Write(lines);
    }

    /// <summary>The suggested name of the file: <c>registro-ospiti-ch-2026-01-01-2026-03-31.csv</c> (the first and the last day of the period).</summary>
    public static string FileName(DateOnly from, DateOnly to) =>
        $"registro-ospiti-ch-{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv";

    private static GuestRegisterRow RowOf(TicinoStayFacts stay, TicinoGuestFacts guest)
    {
        // A guest without a date of birth, or with one after the arrival, is neither a minor nor an adult here: the cell stays empty, the line stays.
        bool? minor = guest.BirthDate is { } born && born <= stay.Arrival
            ? TicinoPoliceFile.AgeAt(born, stay.Arrival) < TicinoPoliceFile.AdultAge
            : null;

        return new GuestRegisterRow(
            stay.Arrival,
            stay.Departure,
            TicinoPoliceFile.Clean(guest.LastName),
            TicinoPoliceFile.Clean(guest.FirstName),
            guest.BirthDate,
            TicinoCountryCodes.FromCheckIn(guest.CitizenshipCode, guest.CitizenshipName) ?? string.Empty,
            TicinoPoliceFile.DocumentKindOf(guest),
            TicinoPoliceFile.Clean(guest.DocumentNumber),
            TicinoPoliceFile.Clean(guest.Street),
            TicinoPoliceFile.Clean(guest.PostalCode),
            TicinoPoliceFile.Clean(TicinoPoliceFile.DomicileOf(guest)),
            TicinoPoliceFile.ResidenceNationOf(guest) ?? string.Empty,
            TicinoPoliceFile.Clean(stay.VehiclePlate),
            TicinoPoliceFile.Clean(stay.ReferenceNumber),
            minor);
    }

    private static IReadOnlyList<object?> CellsOf(GuestRegisterRow row, Func<string, string> text)
    {
        return
        [
            SwissDate(row.Arrival),
            SwissDate(row.Departure),
            row.LastName,
            row.FirstName,
            row.BirthDate is { } born ? SwissDate(born) : string.Empty,
            row.Nationality,
            DocumentWord(row.DocumentKind, text),
            row.DocumentNumber,
            row.Street,
            row.PostalCode,
            row.City,
            row.Country,
            row.VehiclePlate,
            row.BookingCode,
            MinorWord(row.Minor, text),

            // The signature column of the printed register: always empty, whoever the guest is.
            string.Empty,
        ];
    }

    /// <summary>The cell of the column «minor»: «yes» in the language of the reader, the word «no» of the code, empty while the age is not known.</summary>
    private static string MinorWord(bool? minor, Func<string, string> text)
    {
        return minor switch
        {
            true => text(YesKey),
            false => NoWord,
            null => string.Empty,
        };
    }

    private static string DocumentWord(TicinoDocumentKind? kind, Func<string, string> text)
    {
        return kind switch
        {
            TicinoDocumentKind.Passport => text(PassportKey),
            TicinoDocumentKind.IdentityCard => text(IdentityCardKey),
            TicinoDocumentKind.Other => text(OtherDocumentKey),
            _ => string.Empty,
        };
    }

    private static string SwissDate(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
