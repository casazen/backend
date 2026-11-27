using System.Globalization;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// E31/F01/US01/T01 (NF-130, decision D12b): the guest register of a Swiss property as a clean table. The content is exact: UTF-8 with its byte order mark, <c>;</c>, one header line, no
/// comment line, CRLF after every line, an empty signature column, the dates as <c>dd.MM.yyyy</c>, the nationality and the country as alpha-3 codes, the accents as they are, and a text a
/// spreadsheet would run as a formula written as text. The words are the resource keys of the two languages, but «no», which reads the same in both and is a constant of the code.
/// </summary>
public class GuestRegisterFileTests
{
    private static readonly DateOnly Arrival = new(2026, 11, 19);
    private static readonly DateOnly Departure = new(2026, 11, 21);
    private static readonly ResxDocumentTexts Texts = new();

    private const string ItalianHeader =
        "Arrivo;Partenza;Cognome;Nome;Data di nascita;Nazionalità;Tipo documento;Numero documento;Indirizzo;CAP;Località;Paese;Targa;Codice prenotazione;Minorenne;Firma";

    private const string EnglishHeader =
        "Arrival;Departure;Last name;First name;Date of birth;Nationality;Document type;Document number;Street;Postal code;City;Country;Vehicle plate;Booking code;Minor;Signature";

    // ─── The words: both languages, no key left in the file ───

    [Fact]
    public void Header_IsTheSixteenColumnsOfTheBrief_InItalianAndInEnglish_AndTheSignatureIsTheLastOne()
    {
        var italian = Lines(GuestRegisterFile.Write([], Words("it")));
        var english = Lines(GuestRegisterFile.Write([], Words("en")));

        Assert.Equal([ItalianHeader, string.Empty], italian);
        Assert.Equal([EnglishHeader, string.Empty], english);
        Assert.Equal(16, GuestRegisterFile.HeaderKeys.Count);
        Assert.Equal("GuestRegisterColSignature", GuestRegisterFile.HeaderKeys[^1]);
    }

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void EveryWordOfTheFile_ExistsInTheLanguage_AndIsNeverTheKeyItself(string language)
    {
        // A missing key shows as the key in the downloaded file (the document texts never throw): the resource files must hold all of them, in both languages.
        Assert.Equal(GuestRegisterFile.AllKeys.Count, GuestRegisterFile.AllKeys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in GuestRegisterFile.AllKeys)
        {
            var word = Texts.Get(key, language);
            Assert.False(string.IsNullOrWhiteSpace(word), key);
            Assert.NotEqual(key, word);
            Assert.DoesNotContain("{", word, StringComparison.Ordinal);
            Assert.DoesNotContain(";", word, StringComparison.Ordinal);
        }

        // The errors of the endpoints are in both languages too.
        foreach (var key in new[] { "GuestRegisterNotAvailable", "GuestRegisterFormatInvalid" })
            Assert.NotEqual(key, Texts.Get(key, language));
    }

    [Fact]
    public void EveryResourceKeyOfTheFile_HasAnotherTextInEnglish_TheWordNoIsAConstantOfTheCode()
    {
        // The localization test of the resource files (SharedResourcesLocalizationTests, run by the CI) refuses a key whose English text is the Italian one. «No» reads the same in both
        // languages, so it is a constant of the code (GuestRegisterFile.NoWord) and no resource file holds it; every other word of the file differs between the two languages.
        string[] keys = [.. GuestRegisterFile.AllKeys, "GuestRegisterNotAvailable", "GuestRegisterFormatInvalid"];

        var same = keys.Where(key => Texts.Get(key, "it") == Texts.Get(key, "en")).ToList();

        Assert.True(same.Count == 0, $"The same text in Italian and in English: {string.Join(", ", same)}");
        Assert.Equal("No", GuestRegisterFile.NoWord);
        Assert.Equal("GuestRegisterNo", Texts.Get("GuestRegisterNo", "it"));
        Assert.Equal("GuestRegisterNo", Texts.Get("GuestRegisterNo", "en"));
    }

    // ─── The content ───

    [Fact]
    public void Write_AFamilyStay_IsExactlyTheTable_Bom_Crlf_DatesAsSwissDays_AndAnEmptySignature()
    {
        var stay = Stay(
            Adult(0, "Anna", "Müller", StayGuestType.HeadOfFamily),
            Adult(1, "Luca", "Müller", StayGuestType.FamilyMember) with
            {
                BirthDate = new DateOnly(2016, 5, 6),
                DocumentType = null,
                DocumentTypeCode = null,
                DocumentNumber = null,
            });

        var bytes = GuestRegisterFile.Write(GuestRegisterFile.RowsOf(stay), Words("it"));

        // The byte order mark, then UTF-8: a spreadsheet set to Italian reads the accents.
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(
            [
                ItalianHeader,
                "19.11.2026;21.11.2026;Müller;Anna;04.01.1984;CHE;Passaporto;X1234567;Bahnhofstrasse 5;8001;Zürich;CHE;TI 123456;ABCDE-12345;No;",
                "19.11.2026;21.11.2026;Müller;Luca;06.05.2016;CHE;;;Bahnhofstrasse 5;8001;Zürich;CHE;TI 123456;ABCDE-12345;Sì;",
                string.Empty,
            ],
            Lines(bytes));

        // The signature column is the last one and is empty on every line; no comment, title or note line: the header and one line for each guest.
        Assert.All(Lines(bytes).Skip(1).Take(2), line => Assert.EndsWith(";", line, StringComparison.Ordinal));
        Assert.Equal(2, Lines(bytes).Count(line => line.Length > 0) - 1);
        Assert.DoesNotContain(Lines(bytes), line => line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal));

        // The same table in English: only the words change, never the codes or the dates.
        var english = Lines(GuestRegisterFile.Write(GuestRegisterFile.RowsOf(stay), Words("en")));
        Assert.Equal(EnglishHeader, english[0]);
        Assert.Equal("19.11.2026;21.11.2026;Müller;Anna;04.01.1984;CHE;Passport;X1234567;Bahnhofstrasse 5;8001;Zürich;CHE;TI 123456;ABCDE-12345;No;", english[1]);
        Assert.Equal("19.11.2026;21.11.2026;Müller;Luca;06.05.2016;CHE;;;Bahnhofstrasse 5;8001;Zürich;CHE;TI 123456;ABCDE-12345;Yes;", english[2]);
    }

    [Fact]
    public void Write_APeriodWithNoGuest_IsTheHeaderAlone_AnAnswerAndNotAnError()
    {
        var bytes = GuestRegisterFile.Write([], Words("it"));

        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal([ItalianHeader, string.Empty], Lines(bytes));
        Assert.Empty(GuestRegisterFile.RowsOf(Stay()));
    }

    [Fact]
    public void Write_UmlautsAccentsAndOtherLetters_ArePreservedAsTyped_NothingIsTransliterated()
    {
        // The register is for every canton, and the guests come from everywhere: a UTF-8 file keeps what the check-in kept (the Ticino portal file has its own character set, this one does not).
        var stay = Stay(
            Adult(0, "Zoë", "Müller", StayGuestType.SingleGuest),
            Adult(1, "Élodie", "Dupré-Gérard", StayGuestType.FamilyMember),
            Adult(2, "Łukasz", "Çelik", StayGuestType.FamilyMember),
            Adult(3, "Søren", "Åkesson", StayGuestType.FamilyMember));

        var bytes = GuestRegisterFile.Write(GuestRegisterFile.RowsOf(stay), Words("it"));
        var text = Encoding.UTF8.GetString(bytes, Encoding.UTF8.Preamble.Length, bytes.Length - Encoding.UTF8.Preamble.Length);

        Assert.Contains(";Müller;Zoë;", text, StringComparison.Ordinal);
        Assert.Contains(";Dupré-Gérard;Élodie;", text, StringComparison.Ordinal);
        Assert.Contains(";Çelik;Łukasz;", text, StringComparison.Ordinal);
        Assert.Contains(";Åkesson;Søren;", text, StringComparison.Ordinal);
        Assert.DoesNotContain('?', text);
        Assert.DoesNotContain('�', text);
        Assert.Contains("Località", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ANameASpreadsheetWouldRunAsAFormula_IsWrittenAsText_AndACellWithASemicolonIsQuoted()
    {
        // The names are typed by the guests and the file is opened in a spreadsheet (SR-10): a text that starts with = + - @ is written with a leading apostrophe and never
        // calculated, and a cell with a semicolon or a quote is quoted, never changed.
        var stay = Stay(
            Adult(0, "=1+1", "@SUM(A1:A9)", StayGuestType.SingleGuest),
            Adult(1, "+390212345", "-Rossi", StayGuestType.FamilyMember),
            Adult(2, "Mario", "Dell'Orto; Jr \"Il Grande\"", StayGuestType.FamilyMember));

        var lines = Lines(GuestRegisterFile.Write(GuestRegisterFile.RowsOf(stay), Words("it")));

        Assert.StartsWith("19.11.2026;21.11.2026;'@SUM(A1:A9);'=1+1;", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("19.11.2026;21.11.2026;'-Rossi;'+390212345;", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("19.11.2026;21.11.2026;\"Dell'Orto; Jr \"\"Il Grande\"\"\";Mario;", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_AGuestWhoseDataAreIncomplete_IsStillALine_TheEmptyCellsStayEmpty_NothingIsGuessed()
    {
        // The register lists every guest of the stay: a missing date of birth, an unknown nationality or document, a home the check-in did not ask are empty cells, never a line left out.
        var stay = Stay(
            Adult(0, "Anna", "Müller", StayGuestType.SingleGuest) with
            {
                BirthDate = null,
                CitizenshipCode = null,
                CitizenshipName = "ATLANTIDE",
                DocumentType = null,
                DocumentTypeCode = null,
                DocumentNumber = null,
                Street = null,
                PostalCode = null,
                City = null,
                ResidenceCountryCode = null,
                ResidenceCountryName = null,
            });

        var rows = GuestRegisterFile.RowsOf(stay);

        var line = Assert.Single(Lines(GuestRegisterFile.Write(rows, Words("it"))).Skip(1), l => l.Length > 0);
        Assert.Equal("19.11.2026;21.11.2026;Müller;Anna;;;;;;;;;TI 123456;ABCDE-12345;;", line);
        Assert.Null(Assert.Single(rows).Minor);
        Assert.Null(rows[0].DocumentKind);
    }

    [Fact]
    public void Rows_MinorIsYesUnderEighteenAtTheArrival_NoFromEighteen_AndEmptyForADateAfterTheArrival()
    {
        var stay = Stay(
            Adult(0, "Day", "Before", StayGuestType.SingleGuest) with { BirthDate = Arrival.AddYears(-18).AddDays(1) },
            Adult(1, "Day", "Of", StayGuestType.FamilyMember) with { BirthDate = Arrival.AddYears(-18) },
            Adult(2, "Baby", "Born", StayGuestType.FamilyMember) with { BirthDate = Arrival },
            Adult(3, "Not", "Yet", StayGuestType.FamilyMember) with { BirthDate = Arrival.AddDays(1) });

        var rows = GuestRegisterFile.RowsOf(stay);

        Assert.Equal([(bool?)true, false, true, null], rows.Select(r => r.Minor));
    }

    [Fact]
    public void Rows_TheHomeOfAGuestLivingInItaly_IsTheComuneAndItaly_AndAnotherStateIsItsCode()
    {
        var stay = Stay(
            Adult(0, "Anna", "Rossi", StayGuestType.SingleGuest) with
            {
                ResidenceInItaly = true,
                ResidenceComuneName = "Milano",
                City = "Mailand",
                ResidenceCountryCode = null,
                ResidenceCountryName = null,
            },
            Adult(1, "Hans", "Weber", StayGuestType.FamilyMember) with
            {
                ResidenceInItaly = false,
                City = "München",
                ResidenceCountryCode = null,
                ResidenceCountryName = "GERMANIA",
            });

        var rows = GuestRegisterFile.RowsOf(stay);

        Assert.Equal([("Milano", "ITA"), ("München", "DEU")], rows.Select(r => (r.City, r.Country)));
    }

    [Fact]
    public void Rows_ABookerWithoutCheckInRows_IsOneLine_WithTheDataOfTheBooker()
    {
        // The Ticino file reads the stay this way (the booker stands in for a check-in that has no guest yet): the register does too, from the same reading.
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            CheckInDate = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 1,
            BookingCode = "7Q3KZ8M2XB",
            VehiclePlate = "ZH 998877",
            Guest = new Guest
            {
                FirstName = "Hanna",
                LastName = "Keller",
                DateOfBirth = new DateTime(1990, 3, 2, 0, 0, 0, DateTimeKind.Utc),
                Nationality = "GERMANIA",
                DocumentType = GuestDocumentType.IdentityCard,
                DocumentNumber = "L01X00T47",
                Address = "Seestrasse 12",
                PostalCode = "8002",
                City = "Zürich",
            },
        };

        var rows = GuestRegisterFile.RowsOf(TicinoStayFacts.From(booking, []));

        var row = Assert.Single(rows);
        Assert.Equal(
            ("Keller", "Hanna", new DateOnly(1990, 3, 2), "DEU", TicinoDocumentKind.IdentityCard, "L01X00T47", "Seestrasse 12", "8002", "Zürich"),
            (row.LastName, row.FirstName, row.BirthDate, row.Nationality, row.DocumentKind, row.DocumentNumber, row.Street, row.PostalCode, row.City));
        Assert.Equal(("ZH 998877", "7Q3KZ-8M2XB", Arrival, Departure, false), (row.VehiclePlate, row.BookingCode, row.Arrival, row.Departure, row.Minor));
    }

    [Fact]
    public void Rows_AreInTheOrderOfTheCheckIn_AndWhiteSpaceIsTidied()
    {
        var stay = Stay(
            Adult(2, "Third", "Guest", StayGuestType.FamilyMember),
            Adult(0, "  First \r\n", " Guest\tOne ", StayGuestType.HeadOfFamily),
            Adult(1, "Second", "Guest", StayGuestType.FamilyMember));

        var rows = GuestRegisterFile.RowsOf(stay);

        Assert.Equal(["First", "Second", "Third"], rows.Select(r => r.FirstName));
        Assert.Equal("Guest One", rows[0].LastName);
        Assert.DoesNotContain(Lines(GuestRegisterFile.Write(rows, Words("it"))), line => line.Contains('\t', StringComparison.Ordinal));
    }

    // ─── The period and the name of the file ───

    [Fact]
    public void Period_AYearBothEndsCountedIsAllowed_ADayMoreIsTooLong_AndAnEndBeforeTheStartIsInvalid()
    {
        var from = new DateOnly(2028, 1, 1);

        // 2028 is a leap year: 366 days, both ends counted.
        Assert.Null(GuestRegisterFile.PeriodErrorOf(from, from));
        Assert.Null(GuestRegisterFile.PeriodErrorOf(from, new DateOnly(2028, 12, 31)));
        Assert.Equal(366, new DateOnly(2028, 12, 31).DayNumber - from.DayNumber + 1);
        Assert.Equal("swiss_period_too_long", GuestRegisterFile.PeriodErrorOf(from, new DateOnly(2029, 1, 1)));
        Assert.Equal("swiss_period_invalid", GuestRegisterFile.PeriodErrorOf(from, from.AddDays(-1)));
        Assert.Equal("swiss_period_invalid", GuestRegisterFile.PeriodErrorOf(from, DateOnly.MaxValue));
        Assert.Equal(366, GuestRegisterFile.MaxPeriodDays);
    }

    [Fact]
    public void FileName_NamesTheFirstAndTheLastDay_WhateverTheCultureOfTheServer()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "it-IT", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

                // Formatted from real values under each culture: a day format with a time specifier would throw here, and a culture with another calendar would write other digits.
                Assert.Equal("registro-ospiti-ch-2026-01-01-2026-03-31.csv", GuestRegisterFile.FileName(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)));
                var line = Lines(GuestRegisterFile.Write(GuestRegisterFile.RowsOf(Stay(Adult(0, "Anna", "Müller", StayGuestType.SingleGuest))), Words("it")))[1];
                Assert.StartsWith("19.11.2026;21.11.2026;Müller;Anna;04.01.1984;", line, StringComparison.Ordinal);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Write_MoreLinesThanAFileTakes_IsRefusedByTheWriter_422()
    {
        var rows = Enumerable.Repeat(GuestRegisterFile.RowsOf(Stay(Adult(0, "Anna", "Müller", StayGuestType.SingleGuest)))[0], 10_000).ToList();

        var refused = Assert.Throws<DomainRuleException>(() => GuestRegisterFile.Write(rows, Words("it")));

        Assert.Equal("csv_too_many_rows", refused.Code);
    }

    // ─── Helpers ───

    private static Func<string, string> Words(string language) => key => Texts.Get(key, language);

    /// <summary>The lines of a file, without its byte order mark; the last one is empty because every line, the last too, ends with CRLF.</summary>
    private static string[] Lines(byte[] file)
    {
        Assert.True(file.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        return Encoding.UTF8.GetString(file, Encoding.UTF8.Preamble.Length, file.Length - Encoding.UTF8.Preamble.Length).Split("\r\n");
    }

    private static TicinoStayFacts Stay(params TicinoGuestFacts[] guests) =>
        new(Guid.NewGuid(), "Booker", Arrival, Departure, guests.Length, guests, "ABCDE-12345", "TI 123456");

    /// <summary>A Swiss guest with a passport and a home in Zurich, born on 4 January 1984.</summary>
    private static TicinoGuestFacts Adult(int position, string first, string last, StayGuestType type) => new(
        position,
        type,
        first,
        last,
        new DateOnly(1984, 1, 4),
        "100000241",
        "SVIZZERA",
        false,
        null,
        "SVIZZERA",
        GuestDocumentType.Passport,
        "PASOR",
        "X1234567",
        false,
        null,
        "100000241",
        "SVIZZERA",
        "Bahnhofstrasse 5",
        "8001",
        "Zürich");
}
