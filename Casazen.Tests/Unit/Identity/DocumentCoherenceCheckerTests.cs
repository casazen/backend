using System.Globalization;
using System.Text;
using System.Text.Json;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US02/T02 (NF-190, NF-191, D11): the coherence of the fields of a document. The MRZ and the visual zone must agree on what identifies the holder, after the
/// normalization the MRZ makes of a name and with the century of a date inferred; a field read on one side only is unknown, never a conflict and never an agreement, and
/// no flag of the report says «ok» for an unknown; an expired document, an impossible date, a printed expiry, sex or nationality that is not the MRZ one and a failed
/// check digit are told apart; the flags of <see cref="MrzCheckInConsistency"/> appear (the citizenship typed as the name of a state is compared as its code), a field of the
/// booking with nothing declared is <c>not_applicable</c> (IDENT-DECLARED: neutral in <c>booking_consistency</c>, no pass anywhere else); the report holds no value read on the
/// document, nor does the text of its context. The MRZ of these tests is built with its own check digits and read by the parser of the application.
/// </summary>
public class DocumentCoherenceCheckerTests
{
    private static readonly DocumentFormat Cie = IdentityTestDocuments.Cie();
    private static readonly IdentityDecisionPolicy Policy = IdentityTestDocuments.Policy();
    private static readonly DocumentCoherenceChecker Checker = new();

    private static readonly DocumentCoherenceContext Stay = new(new DateOnly(2026, 12, 10));

    /// <summary>What the guest typed in the data step: the citizenship is the name of the state, as the Alloggiati list writes it (the check-in keeps no code of three letters).</summary>
    private static readonly CheckInIdentity Typed = new("MARIA GIUSEPPINA", "ROSSI", new DateOnly(1985, 3, 12), "CA12345AA", "ITALIA");

    private static ZonedField Printed(string name, string value, DocumentSide side = DocumentSide.Front) =>
        new(name, value, side, name, 0.55, ZonedFieldSources.Viz);

    private static ZonedField FromMrz(string name, string value) =>
        new(name, value, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz);

    private static List<ZonedField> Card(
        string surname = "ROSSI",
        string givenNames = "MARIA GIUSEPPINA",
        string birthDate = "1985-03-12",
        string documentNumber = "CA12345AA",
        string issueDate = "2022-05-15",
        string expiryDate = "2032-03-12",
        string? sex = "F",
        string? nationality = "ITA")
    {
        var fields = new List<ZonedField>
        {
            Printed(ZonedFieldNames.Surname, surname),
            Printed(ZonedFieldNames.GivenNames, givenNames),
            Printed(ZonedFieldNames.BirthDate, birthDate),
            Printed(ZonedFieldNames.DocumentNumber, documentNumber),
            Printed(ZonedFieldNames.IssueDate, issueDate),
            Printed(ZonedFieldNames.ExpiryDate, expiryDate),
        };
        if (sex is not null)
            fields.Add(Printed(ZonedFieldNames.Sex, sex));
        if (nationality is not null)
            fields.Add(Printed(ZonedFieldNames.Nationality, nationality));

        return fields;
    }

    private static MrzReading Reading(MrzExtractionResult? result = null) => new(result ?? IdentityTestDocuments.Mrz(), []);

    private static string Result(DocumentCoherenceReport report, string check, string? field = null) =>
        Assert.Single(report.Checks, entry => entry.Check == check && entry.Field == field).Result;

    private static IdentityDecisionPolicy PolicyWith(string identifyingFields)
    {
        var text = IdentityPolicyTestFiles.Edited(("document.identifyingFields", identifyingFields));
        Assert.True(IdentityConfigurationJson.TryCanonicalize(Encoding.UTF8.GetBytes(text), out var canonical));
        return IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical)!;
    }

    [Fact]
    public void Check_AMatchingCard_PassesEveryCheck()
    {
        var report = Checker.Check(Card(), Reading(), Stay with { Declared = Typed }, Cie, Policy);

        Assert.False(report.IdentifyingConflict);
        Assert.False(report.Expired);
        Assert.True(report.DatesValid);
        Assert.True(report.MrzValid);
        Assert.Empty(report.Codes);
        Assert.All(report.Checks, entry => Assert.Equal(DocumentCoherenceResults.Passed, entry.Result));

        Assert.Equal(0, report.UnknownCount);
        Assert.All(DocumentCoherenceReport.CarriedChecks, check => Assert.Equal(DocumentCoherenceResults.Passed, report.Summary(check)));
        Assert.True(report.AllPassed(Cie.RequiredChecks));

        Assert.Equal(
            [
                DocumentFormatChecks.MrzCheckDigits, DocumentFormatChecks.Expiry, DocumentFormatChecks.SideConsistency, DocumentFormatChecks.FieldCoherence,
                DocumentFormatChecks.BookingConsistency,
            ],
            report.Checks.Select(entry => entry.Check).Distinct());
        Assert.Equal(
            [ZonedFieldNames.Surname, ZonedFieldNames.GivenNames, ZonedFieldNames.BirthDate, ZonedFieldNames.DocumentNumber],
            report.Checks.Where(entry => entry.Check == DocumentFormatChecks.SideConsistency).Select(entry => entry.Field));

        Assert.Equal(
            [ZonedFieldNames.BirthDate, ZonedFieldNames.IssueDate, ZonedFieldNames.ExpiryDate, ZonedFieldNames.Sex, ZonedFieldNames.Nationality],
            report.Checks.Where(entry => entry.Check == DocumentFormatChecks.FieldCoherence).Select(entry => entry.Field));
        Assert.All(report.Checks, entry => Assert.Contains(entry.Check, DocumentFormatChecks.All));
    }

    [Theory]
    [InlineData("DELL'ORTO", "DELL ORTO")]
    [InlineData("DELL’ORTO", "DELL ORTO")]
    [InlineData("DE-LUCA", "DE LUCA")]
    [InlineData("DE LUCA", "DE LUCA")]
    [InlineData("Dell'Orto", "DELL ORTO")]
    [InlineData("D'ANGELO", "DANGELO")]
    [InlineData("MÜLLER", "MULLER")]
    [InlineData("MÜLLER", "MUELLER")]
    [InlineData("MARTÍNEZ", "MARTINEZ")]
    public void Check_APrintedSurname_AgreesWithItsMrzForm(string printed, string mrz)
    {
        var report = Checker.Check(Card(surname: printed), Reading(IdentityTestDocuments.Mrz(surname: mrz)), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
        Assert.False(report.IdentifyingConflict);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentFieldConflict, report.Codes);
    }

    [Fact]
    public void Check_AnAccentedGivenName_AgreesWithItsMrzForm()
    {
        var report = Checker.Check(Card(givenNames: "NICOLÒ ÈMILIO"), Reading(IdentityTestDocuments.Mrz(givenNames: "NICOLO EMILIO")), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.False(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_ADifferentSurname_IsAConflict()
    {
        var report = Checker.Check(Card(surname: "BIANCHI"), Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
        Assert.True(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_AnMrzNameFieldCutAtItsLength_IsNotAConflict()
    {
        var cut = IdentityTestDocuments.Mrz(givenNames: "MARIA GIUSEPPINA ANTONIETTA");
        Assert.Equal("MARIA GIUSEPPINA ANTONI", cut.GivenNames);

        var report = Checker.Check(Card(givenNames: "MARIA GIUSEPPINA ANTONIETTA"), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.False(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_AnMrzNameThatIsAShortenedPrintedOneButIsNotCut_IsAConflict()
    {
        var shortened = IdentityTestDocuments.Mrz(givenNames: "MARIA");

        var report = Checker.Check(Card(givenNames: "MARIA GIUSEPPINA"), Reading(shortened), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.True(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_APrintedNameThatIsShorterThanTheMrzOne_IsAConflict_EvenWhenTheLineIsFull()
    {
        var cut = IdentityTestDocuments.Mrz(givenNames: "MARIA GIUSEPPINA ANTONIETTA");

        var report = Checker.Check(Card(givenNames: "MARIA GIUSEPPINA"), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
    }

    [Fact]
    public void Check_ASurnameCutByTheLine_IsNotAConflict_AndTheGivenNamesLostWithItAreUnknown()
    {
        const string surname = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFG";
        var cut = IdentityTestDocuments.Mrz(surname: surname);
        Assert.Equal(surname[..30], cut.Surname);
        Assert.Null(cut.GivenNames);

        var report = Checker.Check(Card(surname: surname), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.Null(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_ASurnameThatIsTheBeginningOfAnotherWhileTheLineHoldsGivenNames_IsAConflict()
    {
        var cut = IdentityTestDocuments.Mrz(givenNames: "MARIA GIUSEPPINA ANTONIETTA");

        var report = Checker.Check(Card(surname: "ROSSINI", givenNames: "MARIA GIUSEPPINA ANTONIETTA"), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
    }

    [Fact]
    public void Check_SwappedDayAndMonth_IsAnIdentifyingConflict()
    {
        var report = Checker.Check(Card(birthDate: "1985-12-03"), Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
        Assert.True(report.IdentifyingConflict);
        Assert.Equal([IdentityGuidanceCodes.DocumentFieldConflict], report.Codes);
        Assert.True(report.DatesValid);
    }

    [Fact]
    public void Check_ABirthYearInAnotherCentury_IsAConflict()
    {
        var report = Checker.Check(Card(birthDate: "1925-03-12"), Reading(IdentityTestDocuments.Mrz(birth: "250312")), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
    }

    [Fact]
    public void Check_AnExpiryBeforeTheStay_YieldsDocumentExpired()
    {
        var report = Checker.Check(Card(expiryDate: "2026-12-01"), Reading(IdentityTestDocuments.Mrz(expiry: "261201")), Stay, Cie, Policy);

        Assert.True(report.Expired);
        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.Expiry, ZonedFieldNames.ExpiryDate));
        Assert.Equal([IdentityGuidanceCodes.DocumentExpired], report.Codes);
        Assert.False(report.IdentifyingConflict);
    }

    [Theory]
    [InlineData("2026-12-09", true)]
    [InlineData("2026-12-10", false)]
    [InlineData("2026-12-11", false)]
    public void Check_TheDayOfTheExpiry_IsStillValid_TheDayAfterIsNot(string expiry, bool expired)
    {
        var yymmdd = expiry[2..4] + expiry[5..7] + expiry[8..10];

        var report = Checker.Check(Card(expiryDate: expiry), Reading(IdentityTestDocuments.Mrz(expiry: yymmdd)), Stay, Cie, Policy);

        Assert.Equal(expired, report.Expired);
        Assert.Equal(expired, report.Codes.Contains(IdentityGuidanceCodes.DocumentExpired));
    }

    [Fact]
    public void Check_TheExpiryOfTheMrz_WinsOverTheOnePrinted()
    {
        var report = Checker.Check(Card(expiryDate: "2020-01-01"), Reading(), Stay, Cie, Policy);

        Assert.False(report.Expired);
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.Expiry, ZonedFieldNames.ExpiryDate));

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.FieldCoherence, ZonedFieldNames.ExpiryDate));
        Assert.Equal(DocumentCoherenceResults.Failed, report.Summary(DocumentFormatChecks.FieldCoherence));
        Assert.False(report.DatesValid);
        Assert.False(report.AllPassed(Cie.RequiredChecks));
        Assert.False(report.IdentifyingConflict);
        Assert.Empty(report.Codes);
    }

    [Fact]
    public void Check_ASexPrintedDifferentlyFromTheMrz_FailsTheCoherence_ButLeavesTheDatesAlone()
    {
        var report = Checker.Check(Card(sex: "M"), Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.FieldCoherence, ZonedFieldNames.Sex));
        Assert.Equal(DocumentCoherenceResults.Failed, report.Summary(DocumentFormatChecks.FieldCoherence));
        Assert.False(report.AllPassed(Cie.RequiredChecks));
        Assert.True(report.DatesValid);
        Assert.False(report.IdentifyingConflict);
        Assert.Empty(report.Codes);
    }

    [Theory]
    [InlineData("FRA", "ITA", DocumentCoherenceResults.Failed)]
    [InlineData("ITA", "ITA", DocumentCoherenceResults.Passed)]
    [InlineData("DEU", "D", DocumentCoherenceResults.Passed)]
    [InlineData("ITA", "D", DocumentCoherenceResults.Failed)]
    public void Check_ANationalityPrintedDifferentlyFromTheMrz_FailsTheCoherence(string printed, string onTheCard, string expected)
    {
        var report = Checker.Check(Card(nationality: printed), Reading(IdentityTestDocuments.Mrz(nationality: onTheCard)), Stay, Cie, Policy);

        Assert.Equal(expected, Result(report, DocumentFormatChecks.FieldCoherence, ZonedFieldNames.Nationality));
        Assert.Equal(expected, report.Summary(DocumentFormatChecks.FieldCoherence));
    }

    [Fact]
    public void Check_AValueReadOnOneSideOnly_AddsNoEntryToTheCoherence()
    {
        var withoutPrint = Checker.Check(Card(sex: null, nationality: null), Reading(), Stay, Cie, Policy);
        Assert.Equal(
            [ZonedFieldNames.BirthDate, ZonedFieldNames.IssueDate, ZonedFieldNames.ExpiryDate],
            withoutPrint.Checks.Where(entry => entry.Check == DocumentFormatChecks.FieldCoherence).Select(entry => entry.Field));

        var withoutMrz = Checker.Check(Card(), null, Stay, Cie, Policy);
        Assert.Equal(
            [ZonedFieldNames.BirthDate, ZonedFieldNames.IssueDate],
            withoutMrz.Checks.Where(entry => entry.Check == DocumentFormatChecks.FieldCoherence).Select(entry => entry.Field));
    }

    [Fact]
    public void Check_AFieldThatIdentifiesTheHolder_IsNotComparedTwice()
    {
        var report = Checker.Check(Card(expiryDate: "2031-03-12"), Reading(), Stay, Cie, PolicyWith("[\"surname\",\"expiryDate\"]"));

        Assert.True(report.IdentifyingConflict);
        Assert.DoesNotContain(report.Checks, entry => entry.Check == DocumentFormatChecks.FieldCoherence && entry.Field == ZonedFieldNames.ExpiryDate);
    }

    [Fact]
    public void Check_TheExpiryPrinted_IsUsedWhenTheMrzDigitOfTheExpiryFailed()
    {
        var broken = IdentityTestDocuments.Mrz(expiry: "320312");
        broken.ExpirationDate = null;
        broken.CheckDigitsValid = false;

        var report = Checker.Check(Card(expiryDate: "2020-01-01"), Reading(broken), Stay, Cie, Policy);

        Assert.True(report.Expired);
    }

    [Fact]
    public void Check_AWrongCheckDigit_MakesTheMrzInvalid()
    {
        var wrong = IdentityTestDocuments.Mrz(birthDigit: '9');
        Assert.False(wrong.CheckDigitsValid);

        var report = Checker.Check(Card(), Reading(wrong), Stay, Cie, Policy);

        Assert.False(report.MrzValid);
        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.MrzCheckDigits));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
        Assert.Null(report.IdentifyingConflict);
        Assert.False(report.AllPassed(Cie.RequiredChecks));
    }

    [Fact]
    public void DocumentCoherence_DifferentDocumentNumber_IsConflict()
    {
        var report = Checker.Check(Card(documentNumber: "CA54321AA"), Reading(), Stay, Cie, Policy);

        Assert.True(report.IdentifyingConflict);
        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.DocumentNumber));
        Assert.Contains(IdentityGuidanceCodes.DocumentFieldConflict, report.Codes);
        Assert.True(report.MrzValid);
        Assert.False(report.Expired);
        Assert.DoesNotContain(report.Checks, entry => entry.Result == DocumentCoherenceResults.Failed && entry.Check != DocumentFormatChecks.SideConsistency);
    }

    [Fact]
    public void Check_ADocumentNumberWithOtherSpacingAndCase_IsTheSameNumber()
    {
        var report = Checker.Check(Card(documentNumber: "ca 12345-aa"), Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.DocumentNumber));
    }

    [Fact]
    public void Check_AFieldReadOnOneSideOnly_IsUnknown_NeverAConflict()
    {
        var printedOnly = new List<ZonedField> { Printed(ZonedFieldNames.Surname, "ROSSI") };

        var report = Checker.Check(printedOnly, Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.DocumentNumber));
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
        Assert.Null(report.IdentifyingConflict);

        var machineOnly = Checker.Check(Card(), null, Stay, Cie, Policy);

        Assert.All(
            machineOnly.Checks.Where(entry => entry.Check == DocumentFormatChecks.SideConsistency),
            entry => Assert.Equal(DocumentCoherenceResults.Unknown, entry.Result));
        Assert.Null(machineOnly.IdentifyingConflict);
    }

    [Fact]
    public void Check_AValidMrzAndOnlyTheSurnamePrinted_IsNotAPass_AndNoFlagSaysOk()
    {
        var report = Checker.Check([Printed(ZonedFieldNames.Surname, "ROSSI")], Reading(), Stay, Cie, Policy);

        Assert.True(report.MrzValid);
        Assert.False(report.Expired);
        Assert.Null(report.IdentifyingConflict);
        Assert.Null(report.DatesValid);
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.SideConsistency));
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.FieldCoherence));
        // Nothing was declared for the booking: nothing to compare (IDENT-DECLARED), which is neutral and does not make the rest of the document pass.
        Assert.Equal(DocumentCoherenceResults.NotApplicable, report.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.Equal(DocumentCoherenceResults.Passed, report.Summary(DocumentFormatChecks.MrzCheckDigits));
        Assert.Equal(DocumentCoherenceResults.Passed, report.Summary(DocumentFormatChecks.Expiry));
        Assert.Equal(report.Checks.Count(entry => entry.Result == DocumentCoherenceResults.Unknown), report.UnknownCount);
        Assert.True(report.UnknownCount > 0);
        Assert.False(report.AllPassed(Cie.RequiredChecks));
        Assert.Empty(report.Codes);
    }

    [Fact]
    public void Check_NothingRead_IsNeverAPass()
    {
        var report = Checker.Check([], null, Stay, Cie, Policy);

        Assert.False(report.MrzValid);
        Assert.Null(report.IdentifyingConflict);
        Assert.Null(report.Expired);
        Assert.Null(report.DatesValid);
        Assert.Empty(report.Codes);
        Assert.DoesNotContain(report.Checks, entry => entry.Result == DocumentCoherenceResults.Passed);
        Assert.All(report.Checks, entry => Assert.Equal(DocumentCoherenceResults.Unknown, entry.Result));
        Assert.Equal(report.Checks.Count, report.UnknownCount);
        Assert.All(DocumentCoherenceReport.CarriedChecks, check => Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(check)));
        Assert.False(report.AllPassed(Cie.RequiredChecks));
        Assert.Equal(
            [
                DocumentFormatChecks.MrzCheckDigits, DocumentFormatChecks.Expiry, DocumentFormatChecks.SideConsistency, DocumentFormatChecks.FieldCoherence,
                DocumentFormatChecks.BookingConsistency,
            ],
            report.Checks.Select(entry => entry.Check).Distinct());
    }

    [Fact]
    public void Check_AnMrzThatWasNotRead_IsInvalid_NotAssumedValid()
    {
        var report = Checker.Check(Card(), new MrzReading(null, [MrzQualityCodes.MrzNotFound]), Stay, Cie, Policy);

        Assert.False(report.MrzValid);
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.MrzCheckDigits));
    }

    [Fact]
    public void Check_TheFieldsOfTheMrzInTheList_AreUsedWhenNoReadingIsGiven()
    {
        var fields = Card(documentNumber: "CA54321AA");
        fields.Add(FromMrz(ZonedFieldNames.DocumentNumber, "CA12345AA"));
        fields.Add(FromMrz(ZonedFieldNames.Surname, "ROSSI"));

        var report = Checker.Check(fields, null, Stay, Cie, Policy);

        Assert.True(report.IdentifyingConflict);
        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.DocumentNumber));
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
        Assert.False(report.MrzValid);
    }

    [Fact]
    public void Check_TheSameFieldPrintedOnTwoSidesDifferently_IsAConflict()
    {
        var fields = new List<ZonedField>
        {
            Printed(ZonedFieldNames.DocumentNumber, "CA12345AA"),
            Printed(ZonedFieldNames.DocumentNumber, "CA12345AB", DocumentSide.Back),
        };

        var report = Checker.Check(fields, null, Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.DocumentNumber));
        Assert.True(report.IdentifyingConflict);
    }

    [Theory]
    [InlineData("2030-01-01", "2022-05-15", "2032-03-12", false)]
    [InlineData("2026-12-11", "2022-05-15", "2032-03-12", false)]
    [InlineData("2026-12-10", "2026-12-10", "2032-03-12", true)]
    [InlineData("1900-01-01", "2022-05-15", "2032-03-12", false)]
    [InlineData("1905-12-10", "2022-05-15", "2032-03-12", false)]
    [InlineData("1905-12-11", "2022-05-15", "2032-03-12", true)]
    [InlineData("1985-03-12", "2033-01-01", "2032-03-12", false)]
    [InlineData("1985-03-12", "2032-03-12", "2032-03-12", true)]
    public void Check_AnImpossibleDate_SetsDatesValidFalse(string birth, string issue, string expiry, bool valid)
    {
        var report = Checker.Check(Card(birthDate: birth, issueDate: issue, expiryDate: expiry), null, Stay, Cie, Policy);

        Assert.Equal(valid, report.DatesValid);
        Assert.Equal(
            valid,
            report.Checks.Where(entry => entry.Check == DocumentFormatChecks.FieldCoherence).All(entry => entry.Result != DocumentCoherenceResults.Failed));
        Assert.Null(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_ADateThatWasNotRead_IsUnknown_AndTheDatesAreNeitherValidNorInvalid()
    {
        var report = Checker.Check([Printed(ZonedFieldNames.Surname, "ROSSI")], Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.FieldCoherence, ZonedFieldNames.BirthDate));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.FieldCoherence, ZonedFieldNames.IssueDate));
        Assert.Null(report.DatesValid);
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.FieldCoherence));
    }

    [Fact]
    public void Check_TheBookingFlags_AreTheOnesOfMrzCheckInConsistency()
    {
        var typed = new CheckInIdentity("MARIA GIUSEPPINA", "ROSSI", new DateOnly(1990, 1, 1), null, "Italiana");
        var mrz = IdentityTestDocuments.Mrz();
        var expected = MrzCheckInConsistency.Compare(mrz, typed);

        var report = Checker.Check(Card(), Reading(mrz), Stay with { Declared = typed }, Cie, Policy);

        Assert.Equal((true, false, (bool?)null, (bool?)null), (expected.Names, expected.BirthDate, expected.DocumentNumber, expected.Nationality));
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.BookingConsistency, DocumentCoherenceFields.Names));
        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.BirthDate));
        // No number was declared: nothing to compare (IDENT-DECLARED), never a conflict. A citizenship that was declared but cannot be compared stays unknown.
        Assert.Equal(DocumentCoherenceResults.NotApplicable, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.DocumentNumber));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.Nationality));
        Assert.False(report.IdentifyingConflict);
        Assert.Empty(report.Codes);
    }

    [Theory]
    [InlineData("ITALIA", "ITA", DocumentCoherenceResults.Passed)]
    [InlineData("Italia", "ITA", DocumentCoherenceResults.Passed)]
    [InlineData("ITA", "ITA", DocumentCoherenceResults.Passed)]
    [InlineData("GERMANIA", "D", DocumentCoherenceResults.Passed)]
    [InlineData("Germania", "DEU", DocumentCoherenceResults.Passed)]
    [InlineData("FRANCIA", "ITA", DocumentCoherenceResults.Failed)]
    [InlineData("ITALIA", "FRA", DocumentCoherenceResults.Failed)]
    [InlineData("Italiana", "ITA", DocumentCoherenceResults.Unknown)]
    [InlineData("Atlantide", "ITA", DocumentCoherenceResults.Unknown)]
    [InlineData("KOSOVO", "RKS", DocumentCoherenceResults.Unknown)]
    [InlineData("RKS", "RKS", DocumentCoherenceResults.Passed)]
    public void Check_TheCitizenshipTypedAsTheNameOfAState_IsComparedThroughTheExplicitTable(string typed, string onTheCard, string expected)
    {
        var report = Checker.Check(Card(), Reading(IdentityTestDocuments.Mrz(nationality: onTheCard)), Stay with { Declared = Typed with { Citizenship = typed } }, Cie, Policy);

        Assert.Equal(expected, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.Nationality));
    }

    [Fact]
    public void Check_TheBookingFlags_AreUnknown_WhenNoMrzIsRead_WhateverWasTyped()
    {
        // Without an MRZ the check of the booking could not run: unknown, never the neutral value of a field with nothing declared.
        foreach (var report in new[]
                 {
                     Checker.Check(Card(), null, Stay with { Declared = Typed }, Cie, Policy),
                     Checker.Check(Card(), null, Stay, Cie, Policy),
                     Checker.Check(Card(), new MrzReading(null, [MrzQualityCodes.MrzNotFound]), Stay with { Declared = new CheckInIdentity(null, null, null, null, null) }, Cie, Policy),
                 })
        {
            Assert.Equal(4, report.Checks.Count(entry => entry.Check == DocumentFormatChecks.BookingConsistency));
            Assert.All(
                report.Checks.Where(entry => entry.Check == DocumentFormatChecks.BookingConsistency),
                entry => Assert.Equal(DocumentCoherenceResults.Unknown, entry.Result));
            Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.BookingConsistency));
            Assert.False(report.AllPassed(Cie.RequiredChecks));
        }
    }

    [Fact]
    public void Check_TheBookingFlags_AreNotApplicable_WhileNothingIsDeclared_AndACoherentCardPasses()
    {
        // IDENT-DECLARED: nothing declared at all, or only blanks, is nothing to compare: every field of the booking is not_applicable (never a conflict, never a pass by
        // itself), the check is not_applicable, and a card that is coherent on its own passes the checks the format requires.
        foreach (var report in new[]
                 {
                     Checker.Check(Card(), Reading(), Stay, Cie, Policy),
                     Checker.Check(Card(), Reading(), Stay with { Declared = new CheckInIdentity(null, null, null, null, null) }, Cie, Policy),
                     Checker.Check(Card(), Reading(), Stay with { Declared = new CheckInIdentity(" ", "", null, "  ", "\t") }, Cie, Policy),
                 })
        {
            Assert.Equal(
                [DocumentCoherenceFields.Names, ZonedFieldNames.BirthDate, ZonedFieldNames.DocumentNumber, ZonedFieldNames.Nationality],
                report.Checks.Where(entry => entry.Check == DocumentFormatChecks.BookingConsistency).Select(entry => entry.Field));
            Assert.All(
                report.Checks.Where(entry => entry.Check == DocumentFormatChecks.BookingConsistency),
                entry => Assert.Equal(DocumentCoherenceResults.NotApplicable, entry.Result));
            Assert.Equal(DocumentCoherenceResults.NotApplicable, report.Summary(DocumentFormatChecks.BookingConsistency));
            Assert.Equal(0, report.UnknownCount);
            Assert.True(report.AllPassed(Cie.RequiredChecks));
            Assert.False(report.IdentifyingConflict);
            Assert.Empty(report.Codes);
        }
    }

    [Theory]
    [InlineData("MARIA GIUSEPPINA", "ROSSI", null, null, null, DocumentCoherenceFields.Names)]
    [InlineData(null, null, "1985-03-12", null, null, ZonedFieldNames.BirthDate)]
    [InlineData(null, null, null, "ca 12345 aa", null, ZonedFieldNames.DocumentNumber)]
    [InlineData(null, null, null, null, "ITALIA", ZonedFieldNames.Nationality)]
    public void Check_ADeclaredValueEqualToTheDocument_Passes_AndTheFieldsWithNothingDeclaredAreNeutral(
        string? first, string? last, string? birth, string? number, string? citizenship, string declaredField)
    {
        var born = birth is null ? (DateOnly?)null : DateOnly.ParseExact(birth, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var report = Checker.Check(Card(), Reading(), Stay with { Declared = new CheckInIdentity(first, last, born, number, citizenship) }, Cie, Policy);

        Assert.All(
            report.Checks.Where(entry => entry.Check == DocumentFormatChecks.BookingConsistency),
            entry => Assert.Equal(entry.Field == declaredField ? DocumentCoherenceResults.Passed : DocumentCoherenceResults.NotApplicable, entry.Result));
        Assert.Equal(DocumentCoherenceResults.Passed, report.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.True(report.AllPassed(Cie.RequiredChecks));
    }

    [Theory]
    [InlineData("GIOVANNI", "VERDI", null, null, null, DocumentCoherenceFields.Names)]
    [InlineData(null, null, "1990-01-01", null, null, ZonedFieldNames.BirthDate)]
    [InlineData(null, null, null, "CA99999ZZ", null, ZonedFieldNames.DocumentNumber)]
    [InlineData(null, null, null, null, "FRANCIA", ZonedFieldNames.Nationality)]
    public void Check_ADeclaredValueThatDiffers_IsAConflict_WhateverWasNotDeclared(
        string? first, string? last, string? birth, string? number, string? citizenship, string declaredField)
    {
        // Proof (safety, IDENT-DECLARED): a value declared and different from the document fails the check of the booking, also when nothing else was declared.
        var born = birth is null ? (DateOnly?)null : DateOnly.ParseExact(birth, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var report = Checker.Check(Card(), Reading(), Stay with { Declared = new CheckInIdentity(first, last, born, number, citizenship) }, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.BookingConsistency, declaredField));
        Assert.Equal(DocumentCoherenceResults.Failed, report.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.False(report.AllPassed(Cie.RequiredChecks));
    }

    [Fact]
    public void Check_WritesNotApplicable_InTheCheckOfTheBookingOnly()
    {
        // Whatever was read and declared, the neutral value of a field with nothing declared is written in booking_consistency and nowhere else.
        var reports = new[]
        {
            Checker.Check(Card(), Reading(), Stay, Cie, Policy),
            Checker.Check([], null, Stay, Cie, Policy),
            Checker.Check([Printed(ZonedFieldNames.Surname, "ROSSI")], Reading(), Stay, Cie, Policy),
            Checker.Check(Card(surname: "BIANCHI", expiryDate: "2020-01-01", sex: null), Reading(IdentityTestDocuments.Mrz(birthDigit: '9')), Stay, Cie, Policy),
            Checker.Check(Card(), Reading(), Stay with { Declared = Typed with { DocumentNumber = null } }, Cie, Policy),
            Checker.Check(Card(), Reading(), Stay, Cie, Policy with { Document = Policy.Document with { IdentifyingFields = [] } }),
        };

        Assert.Contains(reports.SelectMany(report => report.Checks), entry => entry.Result == DocumentCoherenceResults.NotApplicable);
        Assert.All(
            reports.SelectMany(report => report.Checks).Where(entry => entry.Result == DocumentCoherenceResults.NotApplicable),
            entry => Assert.Equal(DocumentFormatChecks.BookingConsistency, entry.Check));
    }

    [Fact]
    public void Check_ThePolicy_SaysWhichFieldsIdentify()
    {
        var surnameOnly = Checker.Check(Card(documentNumber: "CA54321AA"), Reading(), Stay, Cie, PolicyWith("[\"surname\"]"));
        Assert.False(surnameOnly.IdentifyingConflict);
        Assert.Equal(
            [ZonedFieldNames.Surname],
            surnameOnly.Checks.Where(entry => entry.Check == DocumentFormatChecks.SideConsistency).Select(entry => entry.Field));

        var withExpiry = Checker.Check(Card(expiryDate: "2031-03-12"), Reading(), Stay, Cie, PolicyWith("[\"surname\",\"expiryDate\"]"));
        Assert.True(withExpiry.IdentifyingConflict);
        Assert.Equal(DocumentCoherenceResults.Failed, Result(withExpiry, DocumentFormatChecks.SideConsistency, ZonedFieldNames.ExpiryDate));
    }

    [Fact]
    public void Check_APolicyWithoutIdentifyingFields_IsNotAPolicyThatComparesNothingAndPasses()
    {
        var none = Policy with { Document = Policy.Document with { IdentifyingFields = [] } };

        var report = Checker.Check(Card(documentNumber: "CA54321AA"), Reading(), Stay, Cie, none);

        var entry = Assert.Single(report.Checks, check => check.Check == DocumentFormatChecks.SideConsistency);
        Assert.Equal(DocumentCoherenceResults.Unknown, entry.Result);
        Assert.Null(entry.Field);
        Assert.Null(report.IdentifyingConflict);
        Assert.False(report.AllPassed(Cie.RequiredChecks));
    }

    [Fact]
    public void Check_TheReport_CarriesNoValueRead()
    {
        var typed = new CheckInIdentity("GIOVANNI", "VERDI", new DateOnly(1970, 6, 6), "ZZ99999ZZ", "FRA");
        var report = Checker.Check(
            Card(surname: "BIANCHI", givenNames: "LUCIA", birthDate: "1991-02-02", documentNumber: "CB11111BB"),
            Reading(),
            Stay with { Declared = typed },
            Cie,
            Policy);
        Assert.True(report.IdentifyingConflict);

        var json = JsonSerializer.Serialize(report);

        Assert.Contains("IdentifyingConflict", json, StringComparison.Ordinal);
        Assert.DoesNotContain("UnknownCount", json, StringComparison.Ordinal);
        foreach (var value in new[]
                 {
                     "ROSSI", "BIANCHI", "VERDI", "MARIA", "GIUSEPPINA", "LUCIA", "GIOVANNI", "CA12345AA", "CB11111BB", "ZZ99999ZZ", "1985", "1991", "1970",
                     "850312", "12.03", "2032", "2022",
                 })
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);

        Assert.All(report.Checks, entry =>
        {
            Assert.Contains(entry.Check, DocumentFormatChecks.All);
            Assert.Contains(entry.Result, DocumentCoherenceResults.All);
            Assert.True(entry.Field is null || ZonedFieldNames.All.Contains(entry.Field) || DocumentCoherenceFields.All.Contains(entry.Field));
        });
        Assert.All(report.Codes, code => Assert.Contains(code, IdentityGuidanceCodes.All));
    }

    [Fact]
    public void DocumentCoherenceContext_ToString_NeverShowsWhatTheGuestTyped()
    {
        var typed = new CheckInIdentity("MARIA GIUSEPPINA", "ROSSI", new DateOnly(1985, 3, 12), "CA12345AA", "ITALIA");
        var context = new DocumentCoherenceContext(new DateOnly(2026, 12, 10), typed);
        var log = new CapturingLogger<DocumentCoherenceChecker>();
        log.LogInformation("The context of a check {Context}", context);

        var written = new[] { context.ToString(), $"{context}", string.Format(CultureInfo.InvariantCulture, "{0}", context), log.AllOutput };

        Assert.All(written, text =>
        {
            Assert.Contains("2026-12-10", text, StringComparison.Ordinal);
            foreach (var value in new[] { "MARIA", "GIUSEPPINA", "ROSSI", "1985", "03-12", "12.03", "CA12345AA", "ITALIA" })
                Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("typed", context.ToString(), StringComparison.Ordinal);
        Assert.Contains("none", new DocumentCoherenceContext(new DateOnly(2026, 12, 10)).ToString(), StringComparison.Ordinal);
    }

    private static List<DocumentCoherenceCheck> AllPassedEntries() =>
    [
        new(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.Passed),
        new(DocumentFormatChecks.Expiry, DocumentCoherenceResults.Passed, ZonedFieldNames.ExpiryDate),
        new(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Passed, ZonedFieldNames.Surname),
        new(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Passed, ZonedFieldNames.BirthDate),
        new(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.Passed, ZonedFieldNames.BirthDate),
        new(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.Passed, ZonedFieldNames.IssueDate),
        new(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.Passed, DocumentCoherenceFields.Names),
    ];

    private static DocumentCoherenceReport ReportWith(string check, string result) =>
        DocumentCoherenceReport.FromChecks(AllPassedEntries().Select(entry => entry.Check == check ? entry with { Result = result } : entry).ToList());

    [Fact]
    public void AllPassed_NeedsEveryEntryOfEveryRequiredCheckToHavePassed()
    {
        Assert.True(DocumentCoherenceReport.FromChecks(AllPassedEntries()).AllPassed(Cie.RequiredChecks));

        foreach (var spoiled in new[] { DocumentCoherenceResults.Unknown, DocumentCoherenceResults.Failed, "maybe" })
        {
            for (var index = 0; index < AllPassedEntries().Count; index++)
            {
                var entries = AllPassedEntries();
                entries[index] = entries[index] with { Result = spoiled };

                Assert.False(DocumentCoherenceReport.FromChecks(entries).AllPassed(Cie.RequiredChecks), $"entry {index} {spoiled}");
            }
        }
    }

    [Fact]
    public void NotApplicable_SatisfiesTheRequirementOfBookingConsistencyOnly_AndNeverMakesAnotherCheckPass()
    {
        // Proof (safety, IDENT-DECLARED): over every check the report carries and every result (and one nobody knows), a report whose every other entry passed holds for the
        // format only when that check passed, or is booking_consistency with nothing declared. not_applicable anywhere else is no pass: its check is unknown and its flag
        // never says «ok».
        var results = DocumentCoherenceResults.All.Append("maybe").ToList();
        foreach (var check in DocumentCoherenceReport.CarriedChecks)
        {
            foreach (var result in results)
            {
                var report = ReportWith(check, result);
                var holds = result == DocumentCoherenceResults.Passed
                    || (result == DocumentCoherenceResults.NotApplicable && check == DocumentFormatChecks.BookingConsistency);

                Assert.True(holds == report.AllPassed(Cie.RequiredChecks), $"{check}={result}");
                Assert.True(holds == report.AllPassed([DocumentFormatChecks.MrzCheckDigits, check]), $"{check}={result} with the digits");
            }

            var neutral = ReportWith(check, DocumentCoherenceResults.NotApplicable);
            Assert.Equal(
                check == DocumentFormatChecks.BookingConsistency ? DocumentCoherenceResults.NotApplicable : DocumentCoherenceResults.Unknown,
                neutral.Summary(check));
        }

        // The flags of the other checks never take not_applicable for «ok».
        Assert.Null(ReportWith(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.NotApplicable).IdentifyingConflict);
        Assert.Null(ReportWith(DocumentFormatChecks.Expiry, DocumentCoherenceResults.NotApplicable).Expired);
        Assert.Null(ReportWith(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.NotApplicable).DatesValid);
        Assert.False(ReportWith(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.NotApplicable).MrzValid);

        // One entry not_applicable next to passed ones: the booking passes on what was compared; any other check is not told.
        List<DocumentCoherenceCheck> mixed =
        [
            new(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Passed, ZonedFieldNames.Surname),
            new(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.NotApplicable, ZonedFieldNames.BirthDate),
            new(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.Passed, DocumentCoherenceFields.Names),
            new(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.NotApplicable, ZonedFieldNames.DocumentNumber),
        ];
        var partly = DocumentCoherenceReport.FromChecks(mixed);
        Assert.Equal(DocumentCoherenceResults.Unknown, partly.Summary(DocumentFormatChecks.SideConsistency));
        Assert.Equal(DocumentCoherenceResults.Passed, partly.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.Null(partly.IdentifyingConflict);
        Assert.Equal(0, partly.UnknownCount);
    }

    [Fact]
    public void AllPassed_NothingDeclaredAlone_VouchesForNothing()
    {
        // The neutral value satisfies the requirement of the booking, it never makes a document coherent by itself: at least one required check must have passed.
        var onlyTheBooking = DocumentCoherenceReport.FromChecks([new(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.NotApplicable, DocumentCoherenceFields.Names)]);
        Assert.False(onlyTheBooking.AllPassed([DocumentFormatChecks.BookingConsistency]));
        Assert.False(onlyTheBooking.AllPassed([DocumentFormatChecks.Layout, DocumentFormatChecks.BookingConsistency]));

        var withTheDigits = DocumentCoherenceReport.FromChecks(
        [
            new(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.Passed),
            new(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.NotApplicable, DocumentCoherenceFields.Names),
        ]);
        Assert.True(withTheDigits.AllPassed([DocumentFormatChecks.MrzCheckDigits, DocumentFormatChecks.BookingConsistency]));
    }

    [Fact]
    public void AllPassed_ACheckWithoutEntries_AnEmptyList_AndANameOfNoCheck_NeverPass()
    {
        var report = DocumentCoherenceReport.FromChecks(AllPassedEntries());

        var withoutExpiry = DocumentCoherenceReport.FromChecks(AllPassedEntries().Where(entry => entry.Check != DocumentFormatChecks.Expiry).ToList());
        Assert.False(withoutExpiry.AllPassed(Cie.RequiredChecks));
        Assert.False(withoutExpiry.AllPassed([DocumentFormatChecks.Expiry]));
        Assert.False(DocumentCoherenceReport.FromChecks([]).AllPassed(Cie.RequiredChecks));

        Assert.False(report.AllPassed([]));
        Assert.False(report.AllPassed([DocumentFormatChecks.Layout, DocumentFormatChecks.Portrait]));
        Assert.True(report.AllPassed([DocumentFormatChecks.Layout, DocumentFormatChecks.Expiry]));

        Assert.False(report.AllPassed([DocumentFormatChecks.Expiry, "expirey"]));
    }

    [Fact]
    public void Summary_IsFailedWhenOneEntryFailed_PassedOnlyWhenEveryEntryPassed_AndUnknownOtherwise()
    {
        List<DocumentCoherenceCheck> entries =
        [
            new(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Passed, ZonedFieldNames.Surname),
            new(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Unknown, ZonedFieldNames.BirthDate),
            new(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.Unknown, ZonedFieldNames.IssueDate),
            new(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.Failed, ZonedFieldNames.BirthDate),
            new(DocumentFormatChecks.Expiry, DocumentCoherenceResults.Passed, ZonedFieldNames.ExpiryDate),
        ];
        var mixed = DocumentCoherenceReport.FromChecks(entries);

        Assert.Equal(DocumentCoherenceResults.Unknown, mixed.Summary(DocumentFormatChecks.SideConsistency));
        Assert.Equal(DocumentCoherenceResults.Failed, mixed.Summary(DocumentFormatChecks.FieldCoherence));
        Assert.Equal(DocumentCoherenceResults.Passed, mixed.Summary(DocumentFormatChecks.Expiry));
        Assert.Equal(DocumentCoherenceResults.Unknown, mixed.Summary(DocumentFormatChecks.MrzCheckDigits));
        Assert.Equal(DocumentCoherenceResults.Unknown, mixed.Summary("no_such_check"));
        Assert.Equal(2, mixed.UnknownCount);
    }

    [Fact]
    public void FromChecks_TheFlagsAndTheCodesComeFromTheEntries_AndAnUnknownEntryIsNeverOk()
    {
        var conflict = ReportWith(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Failed);
        Assert.True(conflict.IdentifyingConflict);
        Assert.Equal([IdentityGuidanceCodes.DocumentFieldConflict], conflict.Codes);
        Assert.Null(ReportWith(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Unknown).IdentifyingConflict);

        var expired = ReportWith(DocumentFormatChecks.Expiry, DocumentCoherenceResults.Failed);
        Assert.True(expired.Expired);
        Assert.Equal([IdentityGuidanceCodes.DocumentExpired], expired.Codes);
        Assert.Null(ReportWith(DocumentFormatChecks.Expiry, DocumentCoherenceResults.Unknown).Expired);

        Assert.False(ReportWith(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.Failed).DatesValid);
        Assert.Null(ReportWith(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.Unknown).DatesValid);

        Assert.False(ReportWith(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.Failed).MrzValid);
        Assert.False(ReportWith(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.Unknown).MrzValid);

        var passed = DocumentCoherenceReport.FromChecks(AllPassedEntries());
        Assert.False(passed.IdentifyingConflict);
        Assert.False(passed.Expired);
        Assert.True(passed.DatesValid);
        Assert.True(passed.MrzValid);
        Assert.Empty(passed.Codes);
    }

    [Fact]
    public async Task Registration_TheCheckerIsOneInstanceForTheWholeApplication()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSelfHostedIdentityVerification();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var checker = provider.GetRequiredService<DocumentCoherenceChecker>();

        Assert.Same(checker, scope.ServiceProvider.GetRequiredService<DocumentCoherenceChecker>());
    }
}
