using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Validation;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// casazen/backend#979: the file of the guests of a Ticino property for the cantonal police (notifiche.ti.ch), checked against the official
/// XSD and CSV model of <c>https://www4.ti.ch/index.php?id=145950</c> (copies in <c>Fixtures/ticino</c>), the minors rule of the user manual
/// and the mandatory fields of the official page.
/// </summary>
public class TicinoPoliceFileTests
{
    private const string Structure = "NL-12345678";
    private static readonly DateOnly Arrival = new(2026, 11, 19);
    private static readonly DateOnly Departure = new(2026, 11, 21);
    private static readonly DateOnly Today = new(2026, 11, 19);

    private static string FixturePath(string name) => Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "ticino", name);

    // ─── XML against the official XSD ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Xml_OfOneGuest_IsValidAgainstTheOfficialXsd_AsPublished()
    {
        var plan = TicinoPoliceFile.Plan(Structure, [Stay(Adult("Anna", "Rossi"))], Today);

        var xml = TicinoPoliceFile.WriteXml(plan.Registrations);

        Assert.Empty(Validate(OfficialSchema(relaxRegistrationCount: false), xml));
        var registration = Assert.Single(XDocument.Load(new MemoryStream(xml)).Root!.Elements("registration"));
        Assert.Equal(
            ["userId", "firstname", "lastname", "birthdate", "nationality", "arrivaldate", "placeofbirth", "street", "zip", "city", "nation",
             "departuredate", "persover14", "persunder14", "documentType", "documentNumber", "carid", "referenceNr"],
            registration.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(Structure, registration.Element("userId")!.Value);
        Assert.Equal("1984-01-04", registration.Element("birthdate")!.Value);
        Assert.Equal("2026-11-19", registration.Element("arrivaldate")!.Value);
        Assert.Equal("CHE", registration.Element("nationality")!.Value);
        Assert.Equal("Passport", registration.Element("documentType")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", Encoding.UTF8.GetString(xml), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Xml_OfSeveralGuests_FitsTheOfficialXsd_SaveForTheOneRegistrationPerFileItDeclares()
    {
        var plan = TicinoPoliceFile.Plan(Structure, [Stay(Adult("Anna", "Rossi"), Adult("Marco", "Rossi", position: 1))], Today);

        var xml = TicinoPoliceFile.WriteXml(plan.Registrations);

        // The published XSD gives <registration> no maxOccurs, so it allows one per file although its root is plural and the portal reads the
        // file "line by line": every registration fits the schema, and only the count is refused by the schema as published.
        Assert.DoesNotContain("maxOccurs", File.ReadAllText(FixturePath("F-Modello_4.xsd")), StringComparison.Ordinal);
        Assert.Empty(Validate(OfficialSchema(relaxRegistrationCount: true), xml));
        Assert.NotEmpty(Validate(OfficialSchema(relaxRegistrationCount: false), xml));
        Assert.Equal(2, XDocument.Load(new MemoryStream(xml)).Root!.Elements("registration").Count());
    }

    [Fact]
    public void Xml_IsNotAvailable_WhenAPostcodeIsNotANumber_OrTheCountryOfResidenceIsUnknown()
    {
        var british = Adult("John", "Smith") with { CitizenshipCode = "100000219", PostalCode = "SW1A 1AA", ResidenceCountryCode = "100000219" };
        var noResidence = Adult("Anna", "Rossi") with { ResidenceCountryCode = null, ResidenceCountryName = null };

        var britishPlan = TicinoPoliceFile.Plan(Structure, [Stay(british)], Today);
        var noResidencePlan = TicinoPoliceFile.Plan(Structure, [Stay(noResidence)], Today);

        Assert.Single(britishPlan.Registrations);
        Assert.False(britishPlan.XmlAvailable);
        Assert.False(noResidencePlan.XmlAvailable);
        Assert.Throws<ArgumentException>(() => TicinoPoliceFile.WriteXml(britishPlan.Registrations));
        // The CSV has no such limit: the postcode is written as given.
        Assert.Contains(";SW1A 1AA;", Encoding.Latin1.GetString(TicinoPoliceFile.WriteCsv(britishPlan.Registrations)), StringComparison.Ordinal);
    }

    // ─── CSV against the official model ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_HasTheHeaderOfTheOfficialModel_ByteForByte_AndItsRowFormat()
    {
        var guest = Adult("Anna", "Rossi") with { DocumentTypeCode = "IDENT", BirthCountryName = "SVIZZERA" };
        var plan = TicinoPoliceFile.Plan(Structure, [Stay(guest)], Today);

        var csv = TicinoPoliceFile.WriteCsv(plan.Registrations);

        var model = File.ReadAllBytes(FixturePath("D-Modello_2.CSV"));
        var modelHeader = model.AsSpan(0, model.AsSpan().IndexOf("\r\n"u8)).ToArray();
        Assert.Equal(modelHeader, csv.AsSpan(0, modelHeader.Length).ToArray());

        var lines = Encoding.Latin1.GetString(csv).Split("\r\n");
        Assert.Equal(2, lines.Length); // header and one row, no line break after the last one (as in the model)
        Assert.Equal(18, lines[1].Split(';').Length);
        Assert.Equal(
            "NL-12345678;Rossi;Anna;04.01.1984;CHE;19.11.2026;21.11.2026;SVIZZERA;;Via Test 1;6900;Lugano;CHE;;0;0;Carta identità;X1234567",
            lines[1]);
        // ISO-8859-1 like the model: «à» is the single byte 0xE0.
        Assert.Contains((byte)0xE0, csv);
        Assert.DoesNotContain((byte)0xC3, csv);
    }

    [Fact]
    public void Csv_KeepsLatinLetters_StripsAccentsLatin1CannotHold_AndNeverBreaksAColumn()
    {
        var guest = Adult("Łukasz", "Müller; Nowak") with { Street = "Ulica Żółta\n5" };
        var plan = TicinoPoliceFile.Plan(Structure, [Stay(guest)], Today);

        var row = Encoding.Latin1.GetString(TicinoPoliceFile.WriteCsv(plan.Registrations)).Split("\r\n")[1];

        Assert.Equal(18, row.Split(';').Length);
        Assert.Contains("Müller, Nowak;Lukasz;", row, StringComparison.Ordinal);
        Assert.Contains(";Ulica Zólta 5;", row, StringComparison.Ordinal);
    }

    // ─── Who is a row: the minors rule of the user manual ───────────────────────────────────────────────

    [Fact]
    public void Plan_NotifiesEveryAdult_AndCountsTheMinorsOnTheHeadOfFamilyOnly()
    {
        var stay = Stay(
            Adult("Anna", "Rossi", position: 0, type: StayGuestType.FamilyMember),
            Adult("Marco", "Rossi", position: 1, type: StayGuestType.HeadOfFamily),
            Child("Luca", age: 16, position: 2),
            Child("Sara", age: 5, position: 3),
            Child("Giulia", age: 14, position: 4),
            Adult("Paolo", "Rossi", position: 5, type: StayGuestType.FamilyMember) with { BirthDate = Arrival.AddYears(-19) });

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        Assert.Empty(plan.Issues);
        Assert.Equal(["Anna", "Marco", "Paolo"], plan.Registrations.Select(r => r.FirstName));
        var head = Assert.Single(plan.Registrations, r => r.FirstName == "Marco");
        Assert.Equal(2, head.PersonsOver14); // 16 and 14 years old
        Assert.Equal(1, head.PersonsUnder14);
        Assert.All(plan.Registrations.Where(r => r != head), r => Assert.Equal((0, 0), (r.PersonsOver14, r.PersonsUnder14)));
    }

    [Fact]
    public void Plan_WithoutAHeadOfFamily_CountsTheMinorsOnTheFirstAdult_AndAStayOfMinorsNotifiesEachOfThem()
    {
        var family = Stay(Adult("Anna", "Rossi"), Child("Luca", age: 10, position: 1));
        var minorsOnly = Stay(Child("Luca", age: 17) with { CitizenshipCode = "100000241", DocumentType = GuestDocumentType.IdentityCard },
            Child("Sara", age: 16, position: 1) with { CitizenshipCode = "100000241", DocumentType = GuestDocumentType.IdentityCard });

        var familyRow = Assert.Single(TicinoPoliceFile.Plan(Structure, [family], Today).Registrations);
        var minorRows = TicinoPoliceFile.Plan(Structure, [minorsOnly], Today).Registrations;

        Assert.Equal((0, 1), (familyRow.PersonsOver14, familyRow.PersonsUnder14));
        Assert.Equal(2, minorRows.Count);
        Assert.All(minorRows, r => Assert.Equal((0, 0), (r.PersonsOver14, r.PersonsUnder14)));
    }

    // ─── Missing data: said per guest, never an invalid file ────────────────────────────────────────────

    [Fact]
    public void Plan_ReportsPerGuestWhatIsMissing_AndLeavesTheWholeStayOutOfTheFile()
    {
        var complete = Stay(Adult("Anna", "Rossi"));
        var incomplete = Stay(
            Adult("Marco", "Bianchi"),
            Adult("Laura", "Bianchi", position: 1) with
            {
                CitizenshipCode = null,
                CitizenshipName = "ATLANTIDE",
                DocumentType = null,
                DocumentTypeCode = null,
            },
            Child("Pietro", age: 8, position: 2) with { CitizenshipCode = null, DocumentType = null },
            Adult("", "Verdi", position: 3) with { BirthDate = null });

        var plan = TicinoPoliceFile.Plan(Structure, [complete, incomplete], Today);

        Assert.Equal(["Anna"], plan.Registrations.Select(r => r.FirstName));
        Assert.All(plan.Issues, i => Assert.Equal(incomplete.BookingId, i.BookingId));
        Assert.Equal([TicinoMissing.Nationality, TicinoMissing.DocumentType], Assert.Single(plan.Issues, i => i.Position == 1).Missing);
        Assert.Equal([TicinoMissing.BirthDate, TicinoMissing.FirstName], Assert.Single(plan.Issues, i => i.Position == 3).Missing);
        // A minor counted on an adult needs no nationality nor document of their own.
        Assert.DoesNotContain(plan.Issues, i => i.Position == 2);
    }

    [Theory]
    [InlineData(1899, 12, 31)]
    [InlineData(2026, 11, 20)]
    public void Plan_ABirthDateBefore1900OrInTheFuture_IsMissing(int year, int month, int day)
    {
        var plan = TicinoPoliceFile.Plan(Structure, [Stay(Adult("Anna", "Rossi") with { BirthDate = new DateOnly(year, month, day) })], Today);

        Assert.Empty(plan.Registrations);
        Assert.Contains(TicinoMissing.BirthDate, Assert.Single(plan.Issues).Missing);
    }

    [Fact]
    public void Plan_FewerGuestsRecordedThanTheBookingCounts_IsReportedForTheStay()
    {
        var stay = Stay(Adult("Anna", "Rossi")) with { NumberOfGuests = 3 };

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        Assert.Empty(plan.Registrations);
        var issue = Assert.Single(plan.Issues);
        Assert.Null(issue.Position);
        Assert.Equal([TicinoMissing.Guests], issue.Missing);
    }

    // ─── NF-126: the data of the file and its character set ─────────────────────────────────────────────

    [Fact]
    public void TicinoCsv_CompleteStay_EqualsExpectedBytes_Test()
    {
        // A complete stay in Lugano: two adults and a child of 10. One row per adult, the code of the booking on each, the plate on the head's
        // row only, the child counted once (on the head, in «Min14»).
        var head = Adult("Marco", "Bernasconi", position: 0, type: StayGuestType.HeadOfFamily) with { BirthDate = new DateOnly(1980, 3, 2) };
        var partner = Adult("Giulia", "Bernasconi", position: 1, type: StayGuestType.FamilyMember) with
        {
            BirthDate = new DateOnly(1982, 7, 15),
            DocumentType = GuestDocumentType.IdentityCard,
            DocumentTypeCode = "IDENT",
            DocumentNumber = "C7654321",
        };
        var child = Child("Luca", age: 10, position: 2) with { LastName = "Bernasconi" };
        var stay = Stay(head, partner, child) with { ReferenceNumber = "ABCDE-12345", VehiclePlate = "TI123456" };

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);
        var file = TicinoPoliceFile.WriteCsvFile(plan.Registrations, TicinoCsvCharset.Windows1252);

        Assert.Empty(plan.Issues);
        var expected = string.Join(
            "\r\n",
            "Id Struttura;Cognome;Nome;Data di nascita;Nazionalità;Data di arrivo;Data di partenza;Luogo di nascita;Numero bollettino;Indirizzo;CAP;" +
            "Domicilio;Nazione;Numero autovettura;Mag14;Min14;Tipo documento;Numero documento",
            "NL-12345678;Bernasconi;Marco;02.03.1980;CHE;19.11.2026;21.11.2026;SVIZZERA;ABCDE-12345;Via Test 1;6900;Lugano;CHE;TI123456;0;1;Passaporto;X1234567",
            "NL-12345678;Bernasconi;Giulia;15.07.1982;CHE;19.11.2026;21.11.2026;SVIZZERA;ABCDE-12345;Via Test 1;6900;Lugano;CHE;;0;0;Carta identità;C7654321");
        Assert.Equal(Windows1252.GetBytes(expected), file.Content);
        Assert.Equal(("windows-1252", 0), (file.Charset, file.TransliteratedCount));

        // The header is the official model's, byte for byte («à» is the single byte 0xE0, as in the model).
        var model = File.ReadAllBytes(FixturePath("D-Modello_2.CSV"));
        var modelHeader = model.AsSpan(0, model.AsSpan().IndexOf("\r\n"u8)).ToArray();
        Assert.Equal(modelHeader, file.Content.AsSpan(0, modelHeader.Length).ToArray());
    }

    [Fact]
    public void TicinoCsv_Windows1252_TransliteratesUnrepresentable_Test()
    {
        var stay = Stay(
            Adult("Łukasz", "Dvořák") with { Street = "Ulica Żółta 5", City = "Gdańsk" },
            Adult("Иван", "Šimek", position: 1));
        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        var windows = TicinoPoliceFile.WriteCsvFile(plan.Registrations, TicinoCsvCharset.Windows1252);
        var utf8 = TicinoPoliceFile.WriteCsvFile(plan.Registrations, TicinoCsvCharset.Utf8);

        // Windows-1252: the letter without its accent where it has one (Ł ř Ż ł ń), «?» only for the letters with nothing to fall back on (the
        // Cyrillic name), counted; «Š» and «á» are letters of the set and stay as typed (Š is 0x8A, which ISO-8859-1 does not have).
        var rows = Windows1252.GetString(windows.Content).Split("\r\n");
        Assert.StartsWith("NL-12345678;Dvorák;Lukasz;", rows[1], StringComparison.Ordinal);
        Assert.Contains(";Ulica Zólta 5;6900;Gdansk;", rows[1], StringComparison.Ordinal);
        Assert.DoesNotContain("?", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("NL-12345678;Šimek;????;", rows[2], StringComparison.Ordinal);
        Assert.Equal(4, rows[2].Count(c => c == '?'));
        Assert.Equal(9, windows.TransliteratedCount);
        Assert.Contains((byte)0x8A, windows.Content);
        Assert.Equal(windows.Content, Windows1252.GetBytes(string.Join("\r\n", rows)));

        // UTF-8 by configuration: every letter as typed, nothing counted, no byte order mark.
        Assert.Equal(("utf-8", 0), (utf8.Charset, utf8.TransliteratedCount));
        var text = Encoding.UTF8.GetString(utf8.Content);
        Assert.Contains("NL-12345678;Dvořák;Łukasz;", text, StringComparison.Ordinal);
        Assert.Contains(";Ulica Żółta 5;6900;Gdańsk;", text, StringComparison.Ordinal);
        Assert.Contains("NL-12345678;Šimek;Иван;", text, StringComparison.Ordinal);
        Assert.False(utf8.Content.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void Plan_AGuestWithoutAddress_IsStillARow_WithTheOptionalColumnsEmpty()
    {
        var guest = Adult("Anna", "Rossi") with
        {
            Street = null,
            PostalCode = null,
            City = null,
            ResidenceInItaly = null,
            ResidenceCountryCode = null,
            ResidenceCountryName = null,
        };

        var plan = TicinoPoliceFile.Plan(Structure, [Stay(guest)], Today);

        Assert.Empty(plan.Issues);
        var row = Assert.Single(plan.Registrations);
        Assert.Equal((string.Empty, string.Empty, string.Empty, string.Empty), (row.Street, row.Zip, row.City, row.Nation));
        var line = Windows1252.GetString(TicinoPoliceFile.WriteCsv(plan.Registrations)).Split("\r\n")[1];
        Assert.Equal("NL-12345678;Rossi;Anna;04.01.1984;CHE;19.11.2026;21.11.2026;SVIZZERA;;;;;;;0;0;Passaporto;X1234567", line);
    }

    [Fact]
    public void Plan_ASecondAdultWithoutDocumentType_KeepsTheWholeStayOut_AndSaysWhatIsMissing()
    {
        var stay = Stay(
            Adult("Marco", "Bernasconi", type: StayGuestType.HeadOfFamily),
            Adult("Giulia", "Bernasconi", position: 1, type: StayGuestType.FamilyMember) with
            {
                DocumentType = null,
                DocumentTypeCode = null,
                DocumentNumber = null,
            },
            Child("Luca", age: 10, position: 2));

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        Assert.Empty(plan.Registrations);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal((1, "Giulia Bernasconi"), (issue.Position, issue.GuestName));
        Assert.Equal([TicinoMissing.DocumentType], issue.Missing);
    }

    [Fact]
    public void CheckInReadiness_TicinoRequiresDocumentOfEveryAdult_Test()
    {
        var arrival = new DateOnly(2026, 11, 19);
        StayGuest[] guests =
        [
            Registered(0, StayGuestType.HeadOfFamily, new DateTime(1980, 3, 2, 0, 0, 0, DateTimeKind.Utc), "X1234567"),
            Registered(1, StayGuestType.FamilyMember, new DateTime(1982, 7, 15, 0, 0, 0, DateTimeKind.Utc), null),
            Registered(2, StayGuestType.FamilyMember, new DateTime(2016, 5, 1, 0, 0, 0, DateTimeKind.Utc), null),
        ];

        var ticino = CheckInReadinessRules.Evaluate(new CheckInReadinessInput(guests, null, arrival, 3, false, [], InTicino: true));
        var italy = CheckInReadinessRules.Evaluate(new CheckInReadinessInput(guests, null, arrival, 3, false, []));

        // In Ticino the second adult needs a document type and number (blocking: the wizard cannot be confirmed); the child of 10 is counted on the
        // head and needs none. Outside Ticino a family member has no document, as in the Alloggiati record.
        Assert.Equal(
            [(1, AlloggiatiRecordRules.FieldDocumentType), (1, AlloggiatiRecordRules.FieldDocumentNumber)],
            DocumentIssues(ticino));
        Assert.All(ticino.Where(i => i.Position == 1), i => Assert.True(i.Blocking));
        Assert.NotEmpty(CheckInReadinessRules.SendBlockers(ticino));
        Assert.Empty(DocumentIssues(italy));
    }

    [Fact]
    public void NotifiedPositions_AreTheAdults_OrEveryMinorOfAStayWithoutAdults()
    {
        var arrival = new DateOnly(2026, 11, 19);

        var family = TicinoPoliceFile.NotifiedPositions(
            [(0, StayGuestType.HeadOfFamily, new DateOnly(1980, 1, 1)), (1, StayGuestType.FamilyMember, new DateOnly(2008, 11, 19)),
             (2, StayGuestType.FamilyMember, new DateOnly(2008, 11, 20)), (3, StayGuestType.FamilyMember, null)],
            arrival);
        var minors = TicinoPoliceFile.NotifiedPositions(
            [(0, StayGuestType.SingleGuest, new DateOnly(2010, 1, 1)), (1, StayGuestType.SingleGuest, new DateOnly(2012, 1, 1))], arrival);

        // 18 on the day of the arrival is an adult; the day after is not; a guest without a birth date is in neither.
        Assert.Equal([0, 1], family.Order());
        Assert.Equal([0, 1], minors.Order());
    }

    // ─── D11: groups of six or more and minors without an adult ─────────────────────────────────────────

    [Fact]
    public void TicinoGroup_LeaderRowWithCounters_Test()
    {
        // A group of 7: the leader and 6 members (3 adults, one of 15, two of 10). The file holds ONE row, the leader's, with the 6 others counted on
        // it: 14 or more in «Mag14» (the adults too), under 14 in «Min14». The plate and the code of the booking are on that row.
        var group = Stay(
            Adult("Laura", "Guida", position: 0, type: StayGuestType.HeadOfGroup),
            Adult("Paolo", "Membro", position: 1, type: StayGuestType.GroupMember) with { DocumentType = null, DocumentTypeCode = null },
            Adult("Sara", "Membro", position: 2, type: StayGuestType.GroupMember) with { DocumentType = null, DocumentTypeCode = null },
            Adult("Ugo", "Membro", position: 3, type: StayGuestType.GroupMember) with { DocumentType = null, DocumentTypeCode = null },
            GroupChild("Teo", age: 15, position: 4),
            GroupChild("Ada", age: 10, position: 5),
            GroupChild("Ivo", age: 10, position: 6));
        var stay = group with { ReferenceNumber = "ABCDE-12345", VehiclePlate = "TI123456" };

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        Assert.Empty(plan.Issues);
        Assert.Empty(plan.Warnings);
        var row = Assert.Single(plan.Registrations);
        Assert.Equal(("Laura", TicinoRowKind.Group, 7), (row.FirstName, row.Kind, row.GroupSize));
        Assert.Equal((4, 2), (row.PersonsOver14, row.PersonsUnder14));
        Assert.Equal(("TI123456", "ABCDE-12345"), (row.CarId, row.ReferenceNr));
        Assert.Equal(
            "NL-12345678;Guida;Laura;04.01.1984;CHE;19.11.2026;21.11.2026;SVIZZERA;ABCDE-12345;Via Test 1;6900;Lugano;CHE;TI123456;4;2;Passaporto;X1234567",
            Windows1252.GetString(TicinoPoliceFile.WriteCsv(plan.Registrations)).Split("\r\n")[1]);

        // The sheet of the group: the 7 members, the leader first, the four data of each, UTF-8 with its byte order mark, every line ended by CRLF (the last
        // one too: the one writer of the files the host opens in a spreadsheet, so the text after the last CRLF is empty).
        var sheet = TicinoPoliceFile.WriteGroupSheet(TicinoPoliceFile.GroupSheetOf(stay)!);
        Assert.True(sheet.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(
            [
                "Nome;Cognome;Data di nascita;Nazionalità",
                "Laura;Guida;04.01.1984;CHE",
                "Paolo;Membro;04.01.1984;CHE",
                "Sara;Membro;04.01.1984;CHE",
                "Ugo;Membro;04.01.1984;CHE",
                "Teo;Membro;18.11.2011;CHE",
                "Ada;Membro;18.11.2016;CHE",
                "Ivo;Membro;18.11.2016;CHE",
                string.Empty,
            ],
            Encoding.UTF8.GetString(sheet.AsSpan(Encoding.UTF8.Preamble.Length)).Split("\r\n"));

        // Only the leader is a row: the check-in asks the document of the leader alone.
        Assert.Equal([0], TicinoPoliceFile.NotifiedPositions(stay.Guests.Select(g => (g.Position, g.Type, g.BirthDate)), Arrival));
    }

    [Fact]
    public void GroupSheet_ANameASpreadsheetWouldRunAsAFormula_IsWrittenAsText_Test()
    {
        // The names are typed by the guests and the sheet is opened in a spreadsheet: a text that starts with = + - @ (or a tab) would be run as a formula, so
        // it is written with a leading apostrophe, as every file the host downloads (CsvWriter); a cell with a semicolon or a quote is quoted, never changed;
        // an accent and the digits of a date are written as they are.
        TicinoGroupMember[] members =
        [
            new("=1+1", "@SUM(A1:A9)", new DateOnly(1984, 1, 4), "CHE"),
            new("+390212345", "-Rossi", new DateOnly(1990, 5, 6), "ITA"),
            new("\t=cmd", "Dell'Orto; Jr \"Il Grande\"", null, "CHE"),
            new("Zoë", "Müller", new DateOnly(2001, 2, 3), "DEU"),
        ];

        var sheet = TicinoPoliceFile.WriteGroupSheet(members);

        Assert.True(sheet.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(
            [
                "Nome;Cognome;Data di nascita;Nazionalità",
                "'=1+1;'@SUM(A1:A9);04.01.1984;CHE",
                "'+390212345;'-Rossi;06.05.1990;ITA",
                "'\t=cmd;\"Dell'Orto; Jr \"\"Il Grande\"\"\";;CHE",
                "Zoë;Müller;03.02.2001;DEU",
                string.Empty,
            ],
            Encoding.UTF8.GetString(sheet.AsSpan(Encoding.UTF8.Preamble.Length)).Split("\r\n"));
    }

    [Fact]
    public void Plan_AFamilyOfSeven_IsNotAGroup_AndKeepsTheAdultRowsAndTheMinorCounters()
    {
        var stay = Stay(
            Adult("Marco", "Rossi", position: 0, type: StayGuestType.HeadOfFamily),
            Adult("Anna", "Rossi", position: 1, type: StayGuestType.FamilyMember),
            Adult("Nonna", "Rossi", position: 2, type: StayGuestType.FamilyMember),
            Child("Luca", age: 16, position: 3),
            Child("Sara", age: 12, position: 4),
            Child("Ivo", age: 8, position: 5),
            Child("Ada", age: 3, position: 6));

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        Assert.Null(TicinoPoliceFile.GroupSheetOf(stay));
        Assert.Equal(["Marco", "Anna", "Nonna"], plan.Registrations.Select(r => r.FirstName));
        Assert.All(plan.Registrations, r => Assert.Equal((TicinoRowKind.Guest, 0), (r.Kind, r.GroupSize)));
        var head = Assert.Single(plan.Registrations, r => r.FirstName == "Marco");
        Assert.Equal((1, 3), (head.PersonsOver14, head.PersonsUnder14));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Plan_AGroupOfFive_KeepsTheRuleOfTheAdults()
    {
        var stay = Stay(
            Adult("Laura", "Guida", position: 0, type: StayGuestType.HeadOfGroup),
            Adult("Paolo", "Membro", position: 1, type: StayGuestType.GroupMember),
            Adult("Sara", "Membro", position: 2, type: StayGuestType.GroupMember),
            GroupChild("Teo", age: 15, position: 3),
            GroupChild("Ada", age: 10, position: 4));

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        // Under six persons the adults are rows and the minors are counted on the head of the group.
        Assert.Equal(["Laura", "Paolo", "Sara"], plan.Registrations.Select(r => r.FirstName));
        Assert.Equal((1, 1), (plan.Registrations[0].PersonsOver14, plan.Registrations[0].PersonsUnder14));
        Assert.All(plan.Registrations, r => Assert.Equal(TicinoRowKind.Guest, r.Kind));
        Assert.Null(TicinoPoliceFile.GroupSheetOf(stay));
    }

    [Fact]
    public void Plan_AGroupMemberWithoutNationality_StillNotifiesTheLeader_AndWarnsThatTheSheetIsIncomplete()
    {
        var stay = Stay(
            Adult("Laura", "Guida", position: 0, type: StayGuestType.HeadOfGroup),
            Adult("Paolo", "Membro", position: 1, type: StayGuestType.GroupMember) with { CitizenshipCode = null, CitizenshipName = "ATLANTIDE" },
            GroupChild("Teo", age: 15, position: 2),
            GroupChild("Ada", age: 10, position: 3),
            GroupChild("Ivo", age: 9, position: 4),
            GroupChild("Eva", age: 8, position: 5));

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        var row = Assert.Single(plan.Registrations);
        Assert.Equal((TicinoRowKind.Group, 2, 3), (row.Kind, row.PersonsOver14, row.PersonsUnder14));
        Assert.Equal(TicinoWarnings.GroupSheetIncomplete, Assert.Single(plan.Warnings).Code);
        Assert.Equal(string.Empty, TicinoPoliceFile.GroupSheetOf(stay)![1].Nationality);
    }

    [Fact]
    public void Plan_AGroupMemberWithoutBirthDate_CannotBeCounted_AndKeepsTheGroupOut()
    {
        var stay = Stay(
            Adult("Laura", "Guida", position: 0, type: StayGuestType.HeadOfGroup),
            Adult("Paolo", "Membro", position: 1, type: StayGuestType.GroupMember) with { BirthDate = null },
            GroupChild("Teo", age: 15, position: 2),
            GroupChild("Ada", age: 10, position: 3),
            GroupChild("Ivo", age: 9, position: 4),
            GroupChild("Eva", age: 8, position: 5));

        var plan = TicinoPoliceFile.Plan(Structure, [stay], Today);

        Assert.Empty(plan.Registrations);
        Assert.Equal([TicinoMissing.BirthDate], Assert.Single(plan.Issues, i => i.Position == 1).Missing);
    }

    [Fact]
    public void Plan_AStayOfMinorsOnly_NotifiesEachMinor_AndWarnsThatTheParentsAuthorizationIsNeeded()
    {
        var minors = Stay(
            Child("Luca", age: 17) with { CitizenshipCode = "100000241", DocumentType = GuestDocumentType.IdentityCard },
            Child("Sara", age: 16, position: 1) with { CitizenshipCode = "100000241", DocumentType = GuestDocumentType.IdentityCard });
        var family = Stay(Adult("Anna", "Rossi"), Child("Luca", age: 10, position: 1));

        var plan = TicinoPoliceFile.Plan(Structure, [minors, family], Today);

        Assert.Equal(["Anna", "Luca", "Sara"], plan.Registrations.Select(r => r.FirstName).Order());
        var warning = Assert.Single(plan.Warnings);
        Assert.Equal((minors.BookingId, TicinoWarnings.ParentalAuthorizationNeeded), (warning.BookingId, warning.Code));
    }

    // ─── Codes ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("100000241", null, "CHE")]
    [InlineData("100000100", null, "ITA")]
    [InlineData(null, "SVIZZERA", "CHE")]
    [InlineData(null, "Germania", "DEU")]
    [InlineData(null, "STATI UNITI D'AMERICA", "USA")]
    [InlineData("100000219", "REGNO UNITO", "GBR")]
    [InlineData(null, "che", "CHE")]
    [InlineData("100000999", "APOLIDE", null)]
    [InlineData(null, "ATLANTIDE", null)]
    [InlineData(null, null, null)]
    public void CountryCodes_MapTheAlloggiatiStatesToIsoAlpha3_AndNeverGuess(string? code, string? name, string? expected)
    {
        Assert.Equal(expected, TicinoCountryCodes.FromCheckIn(code, name));
    }

    [Fact]
    public void CountryCodes_CoverEveryCurrentStateOfTheAlloggiatiTable_ButTheThreeWithoutACountry()
    {
        Assert.Equal(233, TicinoCountryCodes.Count);
    }

    [Theory]
    [InlineData("PASOR", null, TicinoDocumentKind.Passport)]
    [InlineData("PASDI", null, TicinoDocumentKind.Passport)]
    [InlineData("IDENT", null, TicinoDocumentKind.IdentityCard)]
    [InlineData("IDELE", null, TicinoDocumentKind.IdentityCard)]
    [InlineData("PATEN", null, TicinoDocumentKind.Other)]
    [InlineData(null, GuestDocumentType.Passport, TicinoDocumentKind.Passport)]
    [InlineData(null, GuestDocumentType.IdentityCard, TicinoDocumentKind.IdentityCard)]
    [InlineData(null, GuestDocumentType.DriversLicense, TicinoDocumentKind.Other)]
    [InlineData(null, null, null)]
    public void DocumentKind_FromTheAlloggiatiCode_OrTheTypeChosenAtTheCheckIn(string? code, GuestDocumentType? type, TicinoDocumentKind? expected)
    {
        Assert.Equal(expected, TicinoPoliceFile.DocumentKindOf(Adult("Anna", "Rossi") with { DocumentTypeCode = code, DocumentType = type }));
    }

    // ─── The structure identifier of the property ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("NL-12345678", "NL-12345678")]
    [InlineData(" nl-1234 5678 ", "NL-12345678")]
    [InlineData("XNA9999", "XNA9999")]
    [InlineData("3982", "3982")]
    public void StructureId_ValidValues_AreNormalized(string input, string normalized)
    {
        Assert.True(TicinoStructureId.IsValid(input));
        Assert.Equal(normalized, TicinoStructureId.Normalize(input));
        Assert.True(new TicinoStructureIdAttribute().IsValid(input));
    }

    [Theory]
    [InlineData("NL")]
    [InlineData("NL--1234")]
    [InlineData("-NL1234")]
    [InlineData("NL-1234-")]
    [InlineData("N/A 1234")]
    [InlineData("NL-123456789012345678")]
    public void StructureId_InvalidValues_AreRefused(string input)
    {
        Assert.False(TicinoStructureId.IsValid(input));
        Assert.False(new TicinoStructureIdAttribute().IsValid(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void StructureId_Blank_IsAllowedOnInput_AndClearsTheValue(string? input)
    {
        Assert.True(new TicinoStructureIdAttribute().IsValid(input));
        Assert.Null(TicinoStructureId.Normalize(input));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    private static List<(int, string)> DocumentIssues(IEnumerable<CheckInIssue> issues) =>
        issues
            .Where(i => i.Field is not null && CheckInReadinessRules.DocumentFields.Contains(i.Field))
            .Select(i => (i.Position ?? -1, i.Field!))
            .ToList();

    /// <summary>A guest of the check-in, complete for the Alloggiati record (born abroad), with a passport when <paramref name="document"/> is given.</summary>
    private static StayGuest Registered(int position, StayGuestType type, DateTime born, string? document) => new()
    {
        Position = position,
        Type = type,
        FirstName = "Test",
        LastName = "Bernasconi",
        Gender = Gender.Female,
        DateOfBirth = born,
        BornInItaly = false,
        BirthCountryName = "SVIZZERA",
        CitizenshipName = "SVIZZERA",
        DocumentType = document is null ? null : GuestDocumentType.Passport,
        DocumentNumber = document ?? string.Empty,
        DocumentIssuePlaceName = document is null ? string.Empty : "SVIZZERA",
    };

    private static TicinoStayFacts Stay(params TicinoGuestFacts[] guests) =>
        new(Guid.NewGuid(), "Booker", Arrival, Departure, guests.Length, guests);

    private static TicinoGuestFacts Adult(string first, string last, int position = 0, StayGuestType type = StayGuestType.SingleGuest) => new(
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
        "Via Test 1",
        "6900",
        "Lugano");

    private static TicinoGuestFacts Child(string first, int age, int position = 0) =>
        Adult(first, "Rossi", position, StayGuestType.FamilyMember) with
        {
            BirthDate = Arrival.AddYears(-age).AddDays(-1),
            DocumentType = null,
            DocumentTypeCode = null,
            DocumentNumber = null,
        };

    /// <summary>A minor member of a group: <paramref name="age"/> years at the arrival, no document.</summary>
    private static TicinoGuestFacts GroupChild(string first, int age, int position) =>
        Child(first, age, position) with { LastName = "Membro", Type = StayGuestType.GroupMember };

    private static XmlSchemaSet OfficialSchema(bool relaxRegistrationCount)
    {
        var xsd = XDocument.Load(FixturePath("F-Modello_4.xsd"));
        if (relaxRegistrationCount)
        {
            XNamespace xs = "http://www.w3.org/2001/XMLSchema";
            xsd.Descendants(xs + "element").Single(e => (string?)e.Attribute("name") == "registration").SetAttributeValue("maxOccurs", "unbounded");
        }

        var schemas = new XmlSchemaSet();
        using var reader = xsd.CreateReader();
        schemas.Add(null, reader);
        schemas.Compile();
        return schemas;
    }

    private static List<string> Validate(XmlSchemaSet schemas, byte[] xml)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read())
        {
        }

        return errors;
    }
}
