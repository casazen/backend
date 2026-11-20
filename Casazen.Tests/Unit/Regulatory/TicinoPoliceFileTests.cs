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
