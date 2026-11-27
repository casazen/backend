using System.Text;
using System.Text.Json;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US02/T02 (NF-190, NF-191, D11): the coherence of the fields of a document. The MRZ and the visual zone must agree on what identifies the holder, after the
/// normalization the MRZ makes of a name and with the century of a date inferred; a field read on one side only is unknown, never a conflict; an expired document, an
/// impossible date and a failed check digit are told apart; the flags of <see cref="MrzCheckInConsistency"/> appear as they are; the report holds no value read on the
/// document. The MRZ of these tests is built with its own check digits and read by the parser of the application.
/// </summary>
public class DocumentCoherenceCheckerTests
{
    private static readonly DocumentFormat Cie = IdentityTestDocuments.Cie();
    private static readonly IdentityDecisionPolicy Policy = IdentityTestDocuments.Policy();
    private static readonly DocumentCoherenceChecker Checker = new();

    /// <summary>The stay of the tests: the check-in is on the 10th of December 2026, and the guest typed nothing yet.</summary>
    private static readonly DocumentCoherenceContext Stay = new(new DateOnly(2026, 12, 10));

    /// <summary>What the guest typed in the data step: the citizenship is the name of the state, as the Alloggiati list writes it (the check-in keeps no code of three letters).</summary>
    private static readonly CheckInIdentity Typed = new("MARIA GIUSEPPINA", "ROSSI", new DateOnly(1985, 3, 12), "CA12345AA", "ITALIA");

    private static ZonedField Printed(string name, string value, DocumentSide side = DocumentSide.Front) =>
        new(name, value, side, name, 0.55, ZonedFieldSources.Viz);

    private static ZonedField FromMrz(string name, string value) =>
        new(name, value, DocumentSide.Back, ZonedFieldZones.Mrz, 0.95, ZonedFieldSources.Mrz);

    /// <summary>What the visual zone of the specimen card shows; every argument can be changed to make the card disagree with its MRZ.</summary>
    private static List<ZonedField> Card(
        string surname = "ROSSI",
        string givenNames = "MARIA GIUSEPPINA",
        string birthDate = "1985-03-12",
        string documentNumber = "CA12345AA",
        string issueDate = "2022-05-15",
        string expiryDate = "2032-03-12") =>
    [
        Printed(ZonedFieldNames.Surname, surname),
        Printed(ZonedFieldNames.GivenNames, givenNames),
        Printed(ZonedFieldNames.BirthDate, birthDate),
        Printed(ZonedFieldNames.DocumentNumber, documentNumber),
        Printed(ZonedFieldNames.IssueDate, issueDate),
        Printed(ZonedFieldNames.ExpiryDate, expiryDate),
    ];

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

        // Nothing is unknown, every check of the report passed, and the one answer for the checks the format requires (the layout and the portrait are the geometry's) is yes.
        Assert.Equal(0, report.UnknownCount);
        Assert.All(DocumentCoherenceReport.CarriedChecks, check => Assert.Equal(DocumentCoherenceResults.Passed, report.Summary(check)));
        Assert.True(report.AllPassed(Cie.RequiredChecks));

        // The five checks of the format that this task carries out are all in the report, in the order of the checks of the format; the fields of the policy have one each.
        Assert.Equal(
            [
                DocumentFormatChecks.MrzCheckDigits, DocumentFormatChecks.Expiry, DocumentFormatChecks.SideConsistency, DocumentFormatChecks.FieldCoherence,
                DocumentFormatChecks.BookingConsistency,
            ],
            report.Checks.Select(entry => entry.Check).Distinct());
        Assert.Equal(
            [ZonedFieldNames.Surname, ZonedFieldNames.GivenNames, ZonedFieldNames.BirthDate, ZonedFieldNames.DocumentNumber],
            report.Checks.Where(entry => entry.Check == DocumentFormatChecks.SideConsistency).Select(entry => entry.Field));
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
        // The name field of a TD1 line has 30 characters: ROSSI<<MARIA<GIUSEPPINA<ANTONI is what is left of the three given names.
        var cut = IdentityTestDocuments.Mrz(givenNames: "MARIA GIUSEPPINA ANTONIETTA");
        Assert.Equal("MARIA GIUSEPPINA ANTONI", cut.GivenNames);

        var report = Checker.Check(Card(givenNames: "MARIA GIUSEPPINA ANTONIETTA"), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.False(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_AnMrzNameThatIsAShortenedPrintedOneButIsNotCut_IsAConflict()
    {
        // The line has room to spare: nothing was cut, so MARIA and MARIA GIUSEPPINA are two different names.
        var shortened = IdentityTestDocuments.Mrz(givenNames: "MARIA");

        var report = Checker.Check(Card(givenNames: "MARIA GIUSEPPINA"), Reading(shortened), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.True(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_APrintedNameThatIsShorterThanTheMrzOne_IsAConflict_EvenWhenTheLineIsFull()
    {
        // Only the MRZ can be cut by its line: a printed name that is the beginning of the MRZ one is a difference.
        var cut = IdentityTestDocuments.Mrz(givenNames: "MARIA GIUSEPPINA ANTONIETTA");

        var report = Checker.Check(Card(givenNames: "MARIA GIUSEPPINA"), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
    }

    [Fact]
    public void Check_ASurnameCutByTheLine_IsNotAConflict_AndTheGivenNamesLostWithItAreUnknown()
    {
        // A surname so long that the 30 characters of the line end inside it: no given name is left in the MRZ.
        const string surname = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFG";
        var cut = IdentityTestDocuments.Mrz(surname: surname);
        Assert.Equal(surname[..30], cut.Surname);
        Assert.Null(cut.GivenNames);

        var report = Checker.Check(Card(surname: surname), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        // No conflict, and not an agreement either: the given names were not compared, so the sides are not said to agree.
        Assert.Null(report.IdentifyingConflict);
    }

    [Fact]
    public void Check_ASurnameThatIsTheBeginningOfAnotherWhileTheLineHoldsGivenNames_IsAConflict()
    {
        // The surname ends at the double filler, so it was not cut, whatever the length of the line.
        var cut = IdentityTestDocuments.Mrz(givenNames: "MARIA GIUSEPPINA ANTONIETTA");

        var report = Checker.Check(Card(surname: "ROSSINI", givenNames: "MARIA GIUSEPPINA ANTONIETTA"), Reading(cut), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
    }

    [Fact]
    public void Check_SwappedDayAndMonth_IsAnIdentifyingConflict()
    {
        // 12 March 1985 in the MRZ, 3 December 1985 on the card.
        var report = Checker.Check(Card(birthDate: "1985-12-03"), Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
        Assert.True(report.IdentifyingConflict);
        Assert.Equal([IdentityGuidanceCodes.DocumentFieldConflict], report.Codes);
        // The date itself is a possible one: a conflict is not an impossible date.
        Assert.True(report.DatesValid);
    }

    [Fact]
    public void Check_ABirthYearInAnotherCentury_IsAConflict()
    {
        // The MRZ has the two digits of the year: 250312 reads as 2025 (a birth is never in the future), and a card that says 1925 is not the same date.
        var report = Checker.Check(Card(birthDate: "1925-03-12"), Reading(IdentityTestDocuments.Mrz(birth: "250312")), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
    }

    [Fact]
    public void Check_AnExpiryBeforeTheStay_YieldsDocumentExpired()
    {
        // Expired on the 1st of December, the stay starts on the 10th: the card says the same date as the MRZ.
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
        // The printed date is read at the local confidence; the one of the MRZ is protected by its own digit.
        var report = Checker.Check(Card(expiryDate: "2020-01-01"), Reading(), Stay, Cie, Policy);

        Assert.False(report.Expired);
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.Expiry, ZonedFieldNames.ExpiryDate));
    }

    [Fact]
    public void Check_TheExpiryPrinted_IsUsedWhenTheMrzDigitOfTheExpiryFailed()
    {
        // The digit of the expiry fails: the MRZ gives no expiry (the reading drops a field whose own digit failed), the printed one is used.
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
        // The birth date of the MRZ is not in a reading whose own digit failed: it cannot be compared, which is not a conflict (and not an agreement).
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
        Assert.Null(report.IdentifyingConflict);
        Assert.False(report.AllPassed(Cie.RequiredChecks));
    }

    [Fact]
    public void DocumentCoherence_DifferentDocumentNumber_IsConflict()
    {
        // The number printed on the front is not the one of the MRZ on the back: a card that was put together, or a number that was misread. Either way a person decides.
        var report = Checker.Check(Card(documentNumber: "CA54321AA"), Reading(), Stay, Cie, Policy);

        Assert.True(report.IdentifyingConflict);
        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.DocumentNumber));
        Assert.Contains(IdentityGuidanceCodes.DocumentFieldConflict, report.Codes);
        // Nothing else is wrong with the card, so the conflict is the one thing the report says: it does not pass silently behind the other checks.
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
        // The card was read for the surname only; the MRZ has everything.
        var printedOnly = new List<ZonedField> { Printed(ZonedFieldNames.Surname, "ROSSI") };

        var report = Checker.Check(printedOnly, Reading(), Stay, Cie, Policy);

        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.GivenNames));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.BirthDate));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.DocumentNumber));
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.SideConsistency, ZonedFieldNames.Surname));
        Assert.Null(report.IdentifyingConflict);

        // And the other way round: the card has everything and the MRZ was not read.
        var machineOnly = Checker.Check(Card(), null, Stay, Cie, Policy);

        Assert.All(
            machineOnly.Checks.Where(entry => entry.Check == DocumentFormatChecks.SideConsistency),
            entry => Assert.Equal(DocumentCoherenceResults.Unknown, entry.Result));
        Assert.Null(machineOnly.IdentifyingConflict);
    }

    [Fact]
    public void Check_AValidMrzAndOnlyTheSurnamePrinted_IsNotAPass_AndNoFlagSaysOk()
    {
        // The MRZ is read and valid, the card was read for the surname only: three of the four fields that identify the holder were never compared, the issue date
        // is only printed and was not read, and the guest has typed nothing yet.
        var report = Checker.Check([Printed(ZonedFieldNames.Surname, "ROSSI")], Reading(), Stay, Cie, Policy);

        // What was checked is said as it is: the digits hold and the MRZ says the card is valid on the day of the stay.
        Assert.True(report.MrzValid);
        Assert.False(report.Expired);
        // What was not checked is not «ok»: no flag takes an unread value for a good one.
        Assert.Null(report.IdentifyingConflict);
        Assert.Null(report.DatesValid);
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.SideConsistency));
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.FieldCoherence));
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.BookingConsistency));
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

        // The MRZ is not valid when it was not read, and every other flag is unknown: none says «ok» for something that was not read.
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
        // The digits are not known from a list of fields.
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
    [InlineData("2030-01-01", "2022-05-15", "2032-03-12", false)] // born in the future
    [InlineData("2026-12-11", "2022-05-15", "2032-03-12", false)] // born the day after the check-in
    [InlineData("2026-12-10", "2026-12-10", "2032-03-12", true)] // born on the day of the check-in: odd, possible
    [InlineData("1900-01-01", "2022-05-15", "2032-03-12", false)] // 126 years old
    [InlineData("1905-12-10", "2022-05-15", "2032-03-12", false)] // 121 on the day of the stay
    [InlineData("1905-12-11", "2022-05-15", "2032-03-12", true)] // 120 on the day of the stay, 121 tomorrow
    [InlineData("1985-03-12", "2033-01-01", "2032-03-12", false)] // issued after it expires
    [InlineData("1985-03-12", "2032-03-12", "2032-03-12", true)] // issued the day it expires: odd, possible
    public void Check_AnImpossibleDate_SetsDatesValidFalse(string birth, string issue, string expiry, bool valid)
    {
        // No MRZ: the dates alone are judged, so no conflict between the sides gets in the way (and nothing is compared, so nothing is said of the sides).
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

        // The MRZ has the birth date and the expiry: those are judged. The issue date is only printed and was not read: unknown, so the dates are not said to be valid.
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.FieldCoherence, ZonedFieldNames.BirthDate));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.FieldCoherence, ZonedFieldNames.IssueDate));
        Assert.Null(report.DatesValid);
        Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(DocumentFormatChecks.FieldCoherence));
    }

    [Fact]
    public void Check_TheBookingFlags_AreTheOnesOfMrzCheckInConsistency()
    {
        // The guest typed the same names, another birth date, no number, and a citizenship that is no name of the table of the states (a demonym): not compared.
        var typed = new CheckInIdentity("MARIA GIUSEPPINA", "ROSSI", new DateOnly(1990, 1, 1), null, "Italiana");
        var mrz = IdentityTestDocuments.Mrz();
        var expected = MrzCheckInConsistency.Compare(mrz, typed);

        var report = Checker.Check(Card(), Reading(mrz), Stay with { Declared = typed }, Cie, Policy);

        Assert.Equal((true, false, (bool?)null, (bool?)null), (expected.Names, expected.BirthDate, expected.DocumentNumber, expected.Nationality));
        Assert.Equal(DocumentCoherenceResults.Passed, Result(report, DocumentFormatChecks.BookingConsistency, DocumentCoherenceFields.Names));
        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.BirthDate));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.DocumentNumber));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.Nationality));
        // What the guest typed is not what two sides of the document say: a booking mismatch is not an identifying conflict.
        Assert.False(report.IdentifyingConflict);
        Assert.Empty(report.Codes);
    }

    [Theory]
    [InlineData("ITALIA", "ITA", DocumentCoherenceResults.Passed)] // the name of the state as the check-in keeps it
    [InlineData("Italia", "ITA", DocumentCoherenceResults.Passed)]
    [InlineData("ITA", "ITA", DocumentCoherenceResults.Passed)] // a code typed as such
    [InlineData("GERMANIA", "D", DocumentCoherenceResults.Passed)] // the German cards write D (ICAO 9303)
    [InlineData("Germania", "DEU", DocumentCoherenceResults.Passed)]
    [InlineData("FRANCIA", "ITA", DocumentCoherenceResults.Failed)] // another state: a real disagreement
    [InlineData("ITALIA", "FRA", DocumentCoherenceResults.Failed)]
    [InlineData("Italiana", "ITA", DocumentCoherenceResults.Unknown)] // not a name of the table: never guessed
    [InlineData("Atlantide", "ITA", DocumentCoherenceResults.Unknown)]
    [InlineData("KOSOVO", "RKS", DocumentCoherenceResults.Unknown)] // a code the table does not have on the card: not compared
    [InlineData("RKS", "RKS", DocumentCoherenceResults.Passed)] // two equal codes the table does not have: as MrzCheckInConsistency compares them
    public void Check_TheCitizenshipTypedAsTheNameOfAState_IsComparedThroughTheExplicitTable(string typed, string onTheCard, string expected)
    {
        var report = Checker.Check(Card(), Reading(IdentityTestDocuments.Mrz(nationality: onTheCard)), Stay with { Declared = Typed with { Citizenship = typed } }, Cie, Policy);

        Assert.Equal(expected, Result(report, DocumentFormatChecks.BookingConsistency, ZonedFieldNames.Nationality));
    }

    [Fact]
    public void Check_TheBookingFlags_AreUnknown_WhileNothingIsTypedOrNoMrzIsRead()
    {
        foreach (var report in new[]
                 {
                     Checker.Check(Card(), Reading(), Stay, Cie, Policy),
                     Checker.Check(Card(), null, Stay with { Declared = Typed }, Cie, Policy),
                 })
        {
            Assert.Equal(4, report.Checks.Count(entry => entry.Check == DocumentFormatChecks.BookingConsistency));
            Assert.All(
                report.Checks.Where(entry => entry.Check == DocumentFormatChecks.BookingConsistency),
                entry => Assert.Equal(DocumentCoherenceResults.Unknown, entry.Result));
        }
    }

    [Fact]
    public void Check_ThePolicy_SaysWhichFieldsIdentify()
    {
        // With the surname alone identifying, a different number is no conflict (it is not compared); with the expiry too, a different expiry is one.
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
        // A card that disagrees with its MRZ on the name, the birth date and the number, with a guest who typed other values still.
        var typed = new CheckInIdentity("GIOVANNI", "VERDI", new DateOnly(1970, 6, 6), "ZZ99999ZZ", "FRA");
        var report = Checker.Check(
            Card(surname: "BIANCHI", givenNames: "LUCIA", birthDate: "1991-02-02", documentNumber: "CB11111BB"),
            Reading(),
            Stay with { Declared = typed },
            Cie,
            Policy);
        Assert.True(report.IdentifyingConflict);

        var json = JsonSerializer.Serialize(report);

        // The flags, the entries and the codes are what is kept; the derived answers (the count of unknowns) are not part of the shape.
        Assert.Contains("IdentifyingConflict", json, StringComparison.Ordinal);
        Assert.DoesNotContain("UnknownCount", json, StringComparison.Ordinal);
        foreach (var value in new[]
                 {
                     "ROSSI", "BIANCHI", "VERDI", "MARIA", "GIUSEPPINA", "LUCIA", "GIOVANNI", "CA12345AA", "CB11111BB", "ZZ99999ZZ", "1985", "1991", "1970",
                     "850312", "12.03", "2032", "2022",
                 })
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);

        // Only the names of the checks, their results, the names of the fields and the codes.
        Assert.All(report.Checks, entry =>
        {
            Assert.Contains(entry.Check, DocumentFormatChecks.All);
            Assert.Contains(entry.Result, DocumentCoherenceResults.All);
            Assert.True(entry.Field is null || ZonedFieldNames.All.Contains(entry.Field) || DocumentCoherenceFields.All.Contains(entry.Field));
        });
        Assert.All(report.Codes, code => Assert.Contains(code, IdentityGuidanceCodes.All));
    }

    /// <summary>The entries of a report in which every check passed: one entry for most checks, two for the ones that have more.</summary>
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

    /// <summary>The report of <see cref="AllPassedEntries"/> where every entry of one check has another result.</summary>
    private static DocumentCoherenceReport ReportWith(string check, string result) =>
        DocumentCoherenceReport.FromChecks(AllPassedEntries().Select(entry => entry.Check == check ? entry with { Result = result } : entry).ToList());

    [Fact]
    public void AllPassed_NeedsEveryEntryOfEveryRequiredCheckToHavePassed()
    {
        Assert.True(DocumentCoherenceReport.FromChecks(AllPassedEntries()).AllPassed(Cie.RequiredChecks));

        // One entry that is not a pass, in any check, and the answer is no: an unknown is no pass, a failure neither, and a result nobody knows is not one.
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
    public void AllPassed_ACheckWithoutEntries_AnEmptyList_AndANameOfNoCheck_NeverPass()
    {
        var report = DocumentCoherenceReport.FromChecks(AllPassedEntries());

        // A required check that has no entry at all was not made.
        var withoutExpiry = DocumentCoherenceReport.FromChecks(AllPassedEntries().Where(entry => entry.Check != DocumentFormatChecks.Expiry).ToList());
        Assert.False(withoutExpiry.AllPassed(Cie.RequiredChecks));
        Assert.False(withoutExpiry.AllPassed([DocumentFormatChecks.Expiry]));
        Assert.False(DocumentCoherenceReport.FromChecks([]).AllPassed(Cie.RequiredChecks));

        // A format that requires nothing this report can vouch for is not a pass either (an empty list is never «all»), and the checks of the geometry are not judged here.
        Assert.False(report.AllPassed([]));
        Assert.False(report.AllPassed([DocumentFormatChecks.Layout, DocumentFormatChecks.Portrait]));
        Assert.True(report.AllPassed([DocumentFormatChecks.Layout, DocumentFormatChecks.Expiry]));

        // A name that is no check of the plan (a typo in a format file) never passes.
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

        // And when every entry passed, each flag says what it means.
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
