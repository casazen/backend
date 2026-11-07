using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The rule of the shares of a property (ADR-012-R4, NF-109, AC-NF109-2): on every day the shares in force add up to exactly 100.00, nobody holds
/// the same property twice at once, and a property whose owners all left has a gap up to today. The import and the readiness both ask it.
/// </summary>
public class OwnerOwnershipTimelineTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static OwnershipPeriod P(Guid owner, decimal share, DateOnly start, DateOnly? end = null) => new(owner, share, start, end);

    [Fact]
    public void Analyze_OneOwnerWithTheWholeProperty_HasNoIssue()
    {
        Assert.Empty(OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2024, 1, 1))], D(2024, 1, 1), Today));
    }

    [Fact]
    public void Analyze_TwoOwnersThatAddUpToOneHundredExactly_HaveNoIssue()
    {
        Assert.Empty(OwnerOwnershipTimeline.Analyze([P(A, 50.50m, D(2024, 1, 1)), P(B, 49.50m, D(2024, 1, 1))], D(2024, 1, 1), Today));
    }

    [Fact]
    public void Analyze_ThreeThirds_AreAShortfall_NeverRoundedToOneHundred()
    {
        var c = Guid.NewGuid();
        var issues = OwnerOwnershipTimeline.Analyze(
            [P(A, 33.33m, D(2024, 1, 1)), P(B, 33.33m, D(2024, 1, 1)), P(c, 33.33m, D(2024, 1, 1))], D(2024, 1, 1), Today);

        var issue = Assert.Single(issues);
        Assert.Equal((OwnershipIssueKind.Shortfall, 99.99m, D(2024, 1, 1), (DateOnly?)Today), (issue.Kind, issue.TotalPercent, issue.From, issue.To));
    }

    [Fact]
    public void Analyze_MoreThanOneHundred_IsAnExcess_OnTheDaysItHolds()
    {
        // B joins on 1 June with 60 more: 160 from then on.
        var issues = OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2024, 1, 1)), P(B, 60m, D(2026, 6, 1))], D(2024, 1, 1), Today);

        var issue = Assert.Single(issues);
        Assert.Equal((OwnershipIssueKind.Excess, 160m, D(2026, 6, 1), (DateOnly?)Today), (issue.Kind, issue.TotalPercent, issue.From, issue.To));
    }

    [Fact]
    public void Analyze_AHandOverWithADayInBetween_IsAGapOfThatDay()
    {
        // A until 31 March, B from 2 April: 1 April belongs to nobody.
        var issues = OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2025, 1, 1), D(2026, 3, 31)), P(B, 100m, D(2026, 4, 2))], D(2025, 1, 1), Today);

        var issue = Assert.Single(issues);
        Assert.Equal((OwnershipIssueKind.Gap, 0m, D(2026, 4, 1), (DateOnly?)D(2026, 4, 1)), (issue.Kind, issue.TotalPercent, issue.From, issue.To));
    }

    [Fact]
    public void Analyze_AHandOverOnConsecutiveDays_IsComplete()
    {
        Assert.Empty(OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2025, 1, 1), D(2026, 3, 31)), P(B, 100m, D(2026, 4, 1))], D(2025, 1, 1), Today));
    }

    [Fact]
    public void Analyze_EveryOwnerLeftBeforeToday_IsAGapUpToToday()
    {
        var issues = OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2024, 1, 1), D(2025, 12, 31))], D(2024, 1, 1), Today);

        var issue = Assert.Single(issues);
        Assert.Equal((OwnershipIssueKind.Gap, D(2026, 1, 1), (DateOnly?)Today), (issue.Kind, issue.From, issue.To));
    }

    [Fact]
    public void Analyze_ThePlannedEndOfTheLastOwner_IsNotAGap()
    {
        // The last owner leaves next year: the days after that are not checked.
        Assert.Empty(OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2024, 1, 1), D(2027, 6, 30))], D(2024, 1, 1), Today));
    }

    [Fact]
    public void Analyze_TheDaysBeforeTheFirstOwner_AreNotChecked()
    {
        Assert.Empty(OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2026, 1, 1))], D(2020, 1, 1), Today));
    }

    [Fact]
    public void Analyze_FromTheFirstDayTheFileTouches_IgnoresAnOlderHole()
    {
        // 2024 is a hole in the stored data; a file that touches 2026 on is judged from 2026.
        var periods = new[] { P(A, 100m, D(2023, 1, 1), D(2023, 12, 31)), P(B, 100m, D(2025, 1, 1)) };

        Assert.NotEmpty(OwnerOwnershipTimeline.Analyze(periods, D(2023, 1, 1), Today));
        Assert.Empty(OwnerOwnershipTimeline.Analyze(periods, D(2026, 1, 1), Today));
    }

    [Fact]
    public void Analyze_TheSameOwnerTwiceOnTheSameDays_IsAnOverlapOfThatOwner()
    {
        var issues = OwnerOwnershipTimeline.Analyze([P(A, 50m, D(2024, 1, 1)), P(A, 50m, D(2025, 1, 1), D(2025, 12, 31))], D(2024, 1, 1), Today);

        var overlap = Assert.Single(issues, i => i.Kind == OwnershipIssueKind.OwnerOverlap);
        Assert.Equal((A, D(2025, 1, 1), (DateOnly?)D(2025, 12, 31)), (overlap.OwnerId, overlap.From, overlap.To));
    }

    [Fact]
    public void Analyze_TwoPeriodsOfTheSameOwnerBackToBack_AreNotAnOverlap()
    {
        var issues = OwnerOwnershipTimeline.Analyze([P(A, 100m, D(2024, 1, 1), D(2025, 12, 31)), P(A, 100m, D(2026, 1, 1))], D(2024, 1, 1), Today);

        Assert.Empty(issues);
    }

    [Fact]
    public void Analyze_NoPeriodAtAll_IsNotAnIssueOfTheTimeline()
    {
        Assert.Empty(OwnerOwnershipTimeline.Analyze([], D(2024, 1, 1), Today));
    }

    [Fact]
    public void IsComplete_AddsUpTheSharesInForceOnADay()
    {
        var periods = new[] { P(A, 70m, D(2024, 1, 1)), P(B, 30m, D(2025, 1, 1)) };

        Assert.False(OwnerOwnershipTimeline.IsComplete(periods, D(2024, 6, 1)));
        Assert.True(OwnerOwnershipTimeline.IsComplete(periods, D(2025, 6, 1)));
    }
}

/// <summary>How the facts of an owner are written and checked: the same rules for the import and for the readiness.</summary>
public class OwnerFieldRulesTests
{
    [Theory]
    [InlineData("IT 01234567897", "01234567897")]
    [InlineData("it01234567897", "01234567897")]
    [InlineData("01234567897", "01234567897")]
    [InlineData("CHE-123.456.789 MWST", "CHE123456789MWST")]
    [InlineData("  ", "")]
    public void NormalizeVat_AnItalianNumberLosesItsPrefix_AndEveryOtherIsCompacted(string raw, string expected)
    {
        Assert.Equal(expected, OwnerFieldRules.NormalizeVat(raw));
    }

    [Theory]
    [InlineData("it60 x054 2811 1010 0000 0123 456", "IT60X0542811101000000123456")]
    [InlineData("  ", "")]
    public void NormalizeIban_IsUpperCaseWithoutSpaces(string raw, string expected)
    {
        Assert.Equal(expected, OwnerFieldRules.NormalizeIban(raw));
    }

    [Theory]
    [InlineData("IT60X0542811101000000123456", true)]
    [InlineData("CH9300762011623852957", true)]
    [InlineData("IT60X0542811101000000123457", false)]
    [InlineData("not an iban", false)]
    public void IsValidIban_ChecksTheLengthAndTheChecksum(string iban, bool valid)
    {
        Assert.Equal(valid, OwnerFieldRules.IsValidIban(OwnerFieldRules.NormalizeIban(iban)));
    }

    [Theory]
    [InlineData("IT60X0542811101000000123456", true)]
    [InlineData("CH9300762011623852957", true)]
    [InlineData("AE070331234567890123456", false)]
    public void IsSepaIban_TheIbanAloneIsEnoughInTheSepaArea(string iban, bool sepa)
    {
        Assert.Equal(sepa, OwnerFieldRules.IsSepaIban(iban));
    }

    [Theory]
    [InlineData("BCITITMM", true)]
    [InlineData("BCITITMMXXX", true)]
    [InlineData("BCITIT", false)]
    [InlineData("12345678", false)]
    public void IsValidBic_EightOrElevenCharacters(string bic, bool valid)
    {
        Assert.Equal(valid, OwnerFieldRules.IsValidBic(bic));
    }

    [Theory]
    [InlineData("IT", true, "IT")]
    [InlineData("it", true, "IT")]
    [InlineData("Svizzera", true, "CH")]
    [InlineData("Switzerland", true, "CH")]
    [InlineData("Italia", true, "IT")]
    [InlineData("Atlantide", false, "")]
    [InlineData("", false, "")]
    public void TryNormalizeCountry_TheIsoCodeOrAKnownName(string text, bool ok, string expected)
    {
        Assert.Equal(ok, OwnerFieldRules.TryNormalizeCountry(text, out var country));
        Assert.Equal(expected, country);
    }

    [Theory]
    [InlineData("persona fisica", OwnerEntityKind.NaturalPerson)]
    [InlineData("Person", OwnerEntityKind.NaturalPerson)]
    [InlineData("società", OwnerEntityKind.LegalEntity)]
    [InlineData("Company", OwnerEntityKind.LegalEntity)]
    [InlineData("persona giuridica", OwnerEntityKind.LegalEntity)]
    public void TryParseKind_UnderstandsItalianAndEnglish(string text, OwnerEntityKind expected)
    {
        Assert.True(OwnerFieldRules.TryParseKind(text, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void IsValidFiscalId_ItalyNeedsAnItalianCode_AnotherCountryAnyIdOfFourCharactersOrMore()
    {
        Assert.True(OwnerFieldRules.IsValidFiscalId("RSSMRA85T10A562S", "IT"));
        Assert.True(OwnerFieldRules.IsValidFiscalId("RSSMRA85T10A562S", null));
        Assert.False(OwnerFieldRules.IsValidFiscalId("756.1234.5678.97", "IT"));
        Assert.True(OwnerFieldRules.IsValidFiscalId("7561234567897", "CH"));
        Assert.False(OwnerFieldRules.IsValidFiscalId("X1", "CH"));
    }

    [Fact]
    public void SplitName_APersonIsItsFirstWordAndTheRest_AnEntityIsKeptWhole()
    {
        Assert.Equal(("Mario", "Rossi Bianchi"), OwnerFieldRules.SplitName("Mario Rossi Bianchi", OwnerEntityKind.NaturalPerson));
        Assert.Equal((string.Empty, "Rossi"), OwnerFieldRules.SplitName("Rossi", OwnerEntityKind.NaturalPerson));
        Assert.Equal((string.Empty, "Immobiliare Rossi S.r.l."), OwnerFieldRules.SplitName("Immobiliare Rossi S.r.l.", OwnerEntityKind.LegalEntity));
    }

    [Fact]
    public void DisplayName_IsTheLegalName_ElseTheFirstAndTheLastName()
    {
        Assert.Equal("Rossi S.r.l.", OwnerFieldRules.DisplayName(new PropertyOwner { LegalName = " Rossi S.r.l. ", FirstName = "x", LastName = "y" }));
        Assert.Equal("Mario Rossi", OwnerFieldRules.DisplayName(new PropertyOwner { FirstName = "Mario", LastName = "Rossi" }));
    }
}

/// <summary>The cells of an owner file: a share, a day, and the header.</summary>
public class OwnerImportParserTests
{
    [Theory]
    [InlineData("50", 50.00)]
    [InlineData("33,33", 33.33)]
    [InlineData("33.33 %", 33.33)]
    [InlineData("100", 100.00)]
    [InlineData("0.01", 0.01)]
    public void TryParseShare_APercentWithAtMostTwoDecimals(string text, double expected)
    {
        Assert.True(OwnerImportParser.TryParseShare(text, out var share));
        Assert.Equal((decimal)expected, share);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100.01")]
    [InlineData("33.333")]
    [InlineData("metà")]
    public void TryParseShare_ZeroNegativeOverOneHundredAndMoreThanTwoDecimalsAreNotShares(string text)
    {
        Assert.False(OwnerImportParser.TryParseShare(text, out _));
    }

    [Theory]
    [InlineData("2026-03-31", 2026, 3, 31)]
    [InlineData("31/03/2026", 2026, 3, 31)]
    [InlineData("31-03-2026", 2026, 3, 31)]
    [InlineData("31.03.2026", 2026, 3, 31)]
    [InlineData("1/4/2026", 2026, 4, 1)]
    [InlineData("46112", 2026, 3, 31)]
    public void TryParseDate_TheFormatsOfAnItalianSheetAndTheSerialOfAnExcelDate(string text, int year, int month, int day)
    {
        Assert.True(OwnerImportParser.TryParseDate(text, out var date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("domani")]
    [InlineData("2026")]
    [InlineData("31/02/2026")]
    [InlineData("2026-13-01")]
    [InlineData("12")]
    public void TryParseDate_WhatIsNotADayIsRefused(string text)
    {
        Assert.False(OwnerImportParser.TryParseDate(text, out _));
    }

    [Fact]
    public void Parse_ColumnsAreUnderstoodInItalianAndEnglish_AndAnUnknownOneIsReportedOnce()
    {
        var table = new List<ImportTableRow>
        {
            new(1, ["Codice proprietario", "Ragione sociale", "Codice fiscale", "Quota %", "Inizio proprietà", "Colore preferito", "Colore preferito"]),
            new(2, ["PRO-1", "Mario Rossi", "RSSMRA85T10A562S", "100", "2026-01-01", "blu", "blu"]),
        };

        var parsed = OwnerImportParser.Parse(table);

        var row = Assert.Single(parsed.Rows);
        Assert.Equal(("PRO-1", "Mario Rossi", "RSSMRA85T10A562S", "100", "2026-01-01"), (row.OwnerKey, row.LegalName, row.FiscalCode, row.SharePercent, row.OwnershipStart));
        Assert.Equal(["Colore preferito"], parsed.UnknownColumns);
    }

    [Fact]
    public void Parse_AnEmailAloneIsNotAColumnThatIdentifiesAnOwner()
    {
        var table = new List<ImportTableRow> { new(1, ["email", "legalName", "sharePercent"]), new(2, ["m@example.com", "Mario Rossi", "100"]) };

        var error = Assert.Throws<Casazen.Core.Exceptions.DomainRuleException>(() => OwnerImportParser.Parse(table));

        Assert.Equal(BulkImportCodes.ColumnsUnrecognized, error.Code);
    }

    [Fact]
    public void Fingerprint_IsTheSameForTheSameContentWhateverTheColumnTitles_AndChangesWithTheOrgAndTheContent()
    {
        var org = Guid.NewGuid();
        var first = OwnerImportParser.Parse([new(1, ["ownerKey", "sharePercent"]), new(2, ["A1", "100"])]);
        var renamed = OwnerImportParser.Parse([new(1, ["Codice proprietario", "Quota"]), new(2, [" A1 ", "100"])]);
        var other = OwnerImportParser.Parse([new(1, ["ownerKey", "sharePercent"]), new(2, ["A1", "50"])]);

        var fingerprint = OwnerImportParser.Fingerprint(org, first.Rows);

        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, OwnerImportParser.Fingerprint(org, renamed.Rows));
        Assert.NotEqual(fingerprint, OwnerImportParser.Fingerprint(org, other.Rows));
        Assert.NotEqual(fingerprint, OwnerImportParser.Fingerprint(Guid.NewGuid(), first.Rows));
    }
}

/// <summary>What is missing on an owner, dimension by dimension (AC-NF109-3, AC-NF109-6): the codes and the fields, never a value.</summary>
public class OwnerReadinessRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static PropertyOwner Complete() => new()
    {
        FirstName = "Mario",
        LastName = "Rossi",
        LegalName = "Mario Rossi",
        Email = "mario@example.com",
        EntityKind = OwnerEntityKind.NaturalPerson,
        TaxResidenceCountry = "IT",
        FiscalCode = "RSSMRA85T10A562S",
        BillingStreet = "Via Roma",
        BillingCivic = "1",
        BillingPostalCode = "00100",
        BillingCity = "Roma",
        BillingProvince = "RM",
        BillingCountry = "IT",
        Iban = "IT60X0542811101000000123456",
        SettlementCurrency = "EUR",
    };

    [Fact]
    public void ACompleteOwner_HasNothingMissing()
    {
        var owner = Complete();

        Assert.Empty(OwnerReadinessRules.Registry(owner));
        Assert.Empty(OwnerReadinessRules.FiscalProfile(owner));
        Assert.Empty(OwnerReadinessRules.Payment(owner));
        Assert.Empty(OwnerReadinessRules.Portal(owner, registryReady: true, ownershipReady: true));
    }

    [Fact]
    public void Registry_NeedsTheNameAndTheKind()
    {
        var owner = new PropertyOwner { FirstName = " ", LastName = string.Empty, Email = "x@example.com" };

        var codes = OwnerReadinessRules.Registry(owner).Select(r => r.Code);

        Assert.Equal([OwnerReadinessReasons.LegalNameMissing, OwnerReadinessReasons.EntityKindMissing], codes);
    }

    [Fact]
    public void FiscalProfile_NamesTheExactMissingFacts_NeverTheirValues()
    {
        var owner = Complete();
        owner.TaxResidenceCountry = null;
        owner.FiscalCode = null;
        owner.BillingStreet = null;
        owner.BillingProvince = null;

        var reasons = OwnerReadinessRules.FiscalProfile(owner);

        Assert.Equal(
            [
                (OwnerReadinessReasons.TaxResidenceMissing, OwnerImportColumns.TaxResidence),
                (OwnerReadinessReasons.FiscalIdMissing, OwnerImportColumns.FiscalCode),
                (OwnerReadinessReasons.BillingAddressIncomplete, OwnerImportColumns.BillingStreet),
                (OwnerReadinessReasons.BillingAddressIncomplete, OwnerImportColumns.BillingProvince),
            ],
            reasons.Select(r => (r.Code, r.Field!)));
        Assert.All(reasons, r => Assert.Equal(OwnerReadinessDimensions.FiscalProfile, r.Dimension));
    }

    [Fact]
    public void FiscalProfile_ALegalEntityNeedsAVatNumberOrAFiscalCode_AWrongOneIsInvalid()
    {
        var company = Complete();
        company.EntityKind = OwnerEntityKind.LegalEntity;
        company.FiscalCode = null;

        Assert.Equal(OwnerImportColumns.VatNumber, Assert.Single(OwnerReadinessRules.FiscalProfile(company)).Field);

        company.VatNumber = "IT01234567897";
        Assert.Empty(OwnerReadinessRules.FiscalProfile(company));

        company.VatNumber = "IT01234567890";
        Assert.Equal(OwnerReadinessReasons.VatNumberInvalid, Assert.Single(OwnerReadinessRules.FiscalProfile(company)).Code);

        var person = Complete();
        person.FiscalCode = "RSSMRA85T10A562X";
        Assert.Equal(OwnerReadinessReasons.FiscalCodeInvalid, Assert.Single(OwnerReadinessRules.FiscalProfile(person)).Code);
    }

    [Fact]
    public void Payment_NeedsAnIbanThatIsValid_TheBicOutsideTheSepaArea_AndTheCurrency()
    {
        var owner = Complete();
        owner.Iban = null;
        owner.SettlementCurrency = null;
        Assert.Equal(
            [OwnerReadinessReasons.IbanMissing, OwnerReadinessReasons.SettlementCurrencyMissing],
            OwnerReadinessRules.Payment(owner).Select(r => r.Code));

        owner.Iban = "IT60X0542811101000000123457";
        owner.SettlementCurrency = "EUR";
        Assert.Equal(OwnerReadinessReasons.IbanInvalid, Assert.Single(OwnerReadinessRules.Payment(owner)).Code);

        // A valid IBAN outside the SEPA area needs the BIC too.
        owner.Iban = "AE070331234567890123456";
        Assert.Equal(OwnerReadinessReasons.BicMissing, Assert.Single(OwnerReadinessRules.Payment(owner)).Code);
        owner.Bic = "EBILAEAD";
        Assert.Empty(OwnerReadinessRules.Payment(owner));
    }

    [Fact]
    public void Portal_NeedsTheRegistryTheOwnershipAndAnAddress_AndNothingOfTheAccounting()
    {
        var owner = Complete();
        owner.Iban = null;
        owner.FiscalCode = null;

        // The fiscal profile and the payment are not ready, and the owner can still be invited.
        Assert.Empty(OwnerReadinessRules.Portal(owner, registryReady: true, ownershipReady: true));

        owner.Email = "not an address";
        Assert.Equal(
            [OwnerReadinessReasons.OwnershipNotReady, OwnerReadinessReasons.EmailInvalid],
            OwnerReadinessRules.Portal(owner, registryReady: true, ownershipReady: false).Select(r => r.Code));
    }

    [Fact]
    public void Mandate_NeedsASignedMandateInForceInsideTheOwnershipPeriod()
    {
        var ownership = Guid.NewGuid();
        var property = Guid.NewGuid();
        var facts = new[] { new OwnerOwnershipFact(ownership, property, new DateOnly(2025, 1, 1), null, 100m) };

        Assert.Equal(OwnerReadinessReasons.MandateMissing, Assert.Single(OwnerReadinessRules.Mandate(facts, [], Today)).Code);

        var noDocument = new[] { new OwnerMandateFact(ownership, new DateOnly(2025, 1, 1), null, HasDocument: false) };
        Assert.Equal(OwnerReadinessReasons.MandateDocumentMissing, Assert.Single(OwnerReadinessRules.Mandate(facts, noDocument, Today)).Code);

        var signed = new[] { new OwnerMandateFact(ownership, new DateOnly(2025, 1, 1), null, HasDocument: true) };
        Assert.Empty(OwnerReadinessRules.Mandate(facts, signed, Today));

        // A mandate that started before the ownership is outside it.
        var early = new[] { new OwnerMandateFact(ownership, new DateOnly(2024, 6, 1), null, HasDocument: true) };
        Assert.Equal(OwnerReadinessReasons.MandateOutsideOwnership, Assert.Single(OwnerReadinessRules.Mandate(facts, early, Today)).Code);

        // A mandate that ended before today covers nothing today.
        var ended = new[] { new OwnerMandateFact(ownership, new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), HasDocument: true) };
        Assert.Equal(OwnerReadinessReasons.MandateMissing, Assert.Single(OwnerReadinessRules.Mandate(facts, ended, Today)).Code);
    }

    [Fact]
    public void Mandate_AnOwnerWhoseOwnershipsAllEnded_HasNoMandateToSpeakOf()
    {
        var facts = new[] { new OwnerOwnershipFact(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2020, 1, 1), new DateOnly(2024, 12, 31), 100m) };

        var reason = Assert.Single(OwnerReadinessRules.Mandate(facts, [], Today));

        Assert.Equal((OwnerReadinessReasons.MandateMissing, (Guid?)null), (reason.Code, reason.PropertyId));
    }

    [Fact]
    public void PortalState_FollowsTheInvitations_AnAcceptedOneWins_ALapsedLinkIsExpired()
    {
        var now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
        OwnerInvitationFact Invitation(OwnerInvitationStatus status, int expiresInDays, int createdDaysAgo) =>
            new(Guid.NewGuid(), status, now.AddDays(expiresInDays), now.AddDays(-createdDaysAgo));

        Assert.Equal(OwnerPortalStates.NotReady, OwnerPortalStateRules.StateOf([], now, portalReady: false));
        Assert.Equal(OwnerPortalStates.Ready, OwnerPortalStateRules.StateOf([], now, portalReady: true));
        Assert.Equal(OwnerPortalStates.Invited, OwnerPortalStateRules.StateOf([Invitation(OwnerInvitationStatus.Pending, 3, 1)], now, true));
        Assert.Equal(OwnerPortalStates.Expired, OwnerPortalStateRules.StateOf([Invitation(OwnerInvitationStatus.Pending, -1, 8)], now, true));
        Assert.Equal(OwnerPortalStates.Revoked, OwnerPortalStateRules.StateOf([Invitation(OwnerInvitationStatus.Revoked, 3, 1)], now, true));
        Assert.Equal(
            OwnerPortalStates.Accepted,
            OwnerPortalStateRules.StateOf([Invitation(OwnerInvitationStatus.Accepted, 3, 5), Invitation(OwnerInvitationStatus.Revoked, 3, 1)], now, true));

        // The latest invitation says: an old revoked one followed by a live one is invited.
        Assert.Equal(
            OwnerPortalStates.Invited,
            OwnerPortalStateRules.StateOf([Invitation(OwnerInvitationStatus.Revoked, 3, 5), Invitation(OwnerInvitationStatus.Pending, 3, 1)], now, true));

        Assert.True(OwnerPortalStateRules.CanBeInvited(OwnerPortalStates.Ready));
        Assert.True(OwnerPortalStateRules.CanBeInvited(OwnerPortalStates.Expired));
        Assert.True(OwnerPortalStateRules.CanBeInvited(OwnerPortalStates.Revoked));
        Assert.False(OwnerPortalStateRules.CanBeInvited(OwnerPortalStates.Invited));
        Assert.False(OwnerPortalStateRules.CanBeInvited(OwnerPortalStates.Accepted));
        Assert.False(OwnerPortalStateRules.CanBeInvited(OwnerPortalStates.NotReady));
    }
}
