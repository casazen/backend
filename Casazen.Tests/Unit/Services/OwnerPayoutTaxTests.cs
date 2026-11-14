using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F05/US02/T03 (NF-110, AC-NF110-4, AC-NF110-5, AC-NF110-7, ADR-018 amended): only a confirmed payout decides a withholding, on the approved rule in
/// force on the payment day and the facts of the payout; a retry never decides twice; a generated tax file is never a filed one.
/// </summary>
public class OwnerPayoutTaxTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    [Fact]
    public async Task OwnerPayout_NoCollectionOrPaymentInterventionCreatesNoWithholding_Test()
    {
        var world = await PayoutWorld.CreateAsync();
        var italian = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m));
        var version = await world.ApprovedVersionAsync(italian, 1000m);

        // No statement operation decided anything: the post, the version, its approval and publication made no evidence.
        await world.Accounting.PublishVersionAsync(OrgId, version, null);
        Assert.Empty(await world.Db.TaxObligationEvidence.ToListAsync());

        // An unknown fact the rule needs blocks the payout: nothing is guessed, nothing is written.
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() => world.Payouts.ConfirmPayoutAsync(OrgId, Payout(italian, version, "k-unknown") with { AgencyCollected = null }, null));
        Assert.Equal((OwnerPayoutErrors.TaxApplicabilityUnresolved, "agencyCollected"), (unknown.Code, unknown.Extensions["field"]));
        Assert.Empty(await world.Db.OwnerPayouts.ToListAsync());

        // The agency neither collected nor intervened: an explicit not-applicable decision, no amount, no tax code, the whole gross paid.
        var result = await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(italian, version, "k-direct") with { AgencyCollected = false }, null);
        Assert.Equal((0m, 800m), (result.Payout.WithholdingAmount, result.Payout.NetAmount));
        var decision = Assert.Single(result.Evidence);
        Assert.Equal((TaxDecision.NotApplicable, OwnerWithholdingRules.NoCollectionOrIntervention), (decision.Decision, decision.ReasonCode));
        Assert.Null(decision.Amount);
        Assert.Null(decision.TaxCode);
        Assert.Equal(("IT", "TEST-IT", "1"), (decision.Jurisdiction, decision.RuleCode, decision.RuleVersion));
        var none = await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.GenerateF24Async(OrgId, 11, 2026));
        Assert.Equal(OwnerPayoutErrors.NoObligations, none.Code);

        // A Swiss owner's rule asks no withholding: never an Italian obligation, F24 or annual data.
        var swiss = await world.OwnerAsync("CH", "EUR", new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 20 });
        var swissVersion = await world.ApprovedVersionAsync(swiss, 500m);
        var swissPayout = await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(swiss, swissVersion, "k-swiss", 400m), null);
        Assert.Equal(OwnerWithholdingRules.RuleWithoutWithholding, Assert.Single(swissPayout.Evidence).ReasonCode);
        Assert.Equal(0m, swissPayout.Payout.WithholdingAmount);
        await Assert.ThrowsAsync<DomainRuleException>(() => world.Payouts.GenerateAnnualDatasetAsync(OrgId, 2026, null));
    }

    [Fact]
    public async Task OwnerPayout_ApplicableItalianRuleUsesGrossBaseAtPayment_Test()
    {
        var world = await PayoutWorld.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m), validTo: new DateOnly(2026, 10, 31));
        var version = await world.ApprovedVersionAsync(owner, 1000m);

        // From November the owner follows version 2 of the rule (26%): the payout of the October statement, paid on 5 November, follows it.
        var next = await world.Fiscal.CreatePolicyAsync(OrgId, Policy("IT", "2", ItalianRule("2", 26m), new DateOnly(2026, 11, 1), null), null);
        await world.Fiscal.ApprovePolicyAsync(OrgId, next.Id, "TEST Commercialista", null);
        await world.Fiscal.SaveProfileAsync(OrgId, owner, Profile("IT", "EUR", next.Id, new DateOnly(2026, 11, 1)), null);

        var command = Payout(owner, version, "k-applicable") with
        {
            Components = new Dictionary<string, decimal> { ["rent"] = 900.05m, ["cleaning"] = 99.90m, ["deposit"] = 200m },
        };
        var result = await world.Payouts.ConfirmPayoutAsync(OrgId, command, "auth0|finance");

        // The base is the components the rule names (the deposit is not one), the rate and rounding those of the rule in force on the payment day.
        var decision = Assert.Single(result.Evidence);
        Assert.Equal(TaxDecision.Applicable, decision.Decision);
        Assert.Equal((999.95m, 26m, "half_up", 259.99m, "1919"), (decision.GrossBase!.Value, decision.RatePercent!.Value, decision.Rounding, decision.Amount!.Value, decision.TaxCode));
        Assert.Equal(("2", 2026, 11, version), (decision.RuleVersion, decision.PeriodYear, decision.PeriodMonth, decision.OwnerStatementVersionId));
        Assert.Equal((259.99m, 540.01m), (result.Payout.WithholdingAmount, result.Payout.NetAmount));

        // The payout settles the owed gross in the owner ledger of November: the net paid and the withholding kept.
        var lines = await world.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.SourceType == OwnerLedgerSourceType.OwnerPayout).ToListAsync();
        Assert.Equal(-800m, lines.Sum(e => e.Amount));
        Assert.Equal(-259.99m, lines.Single(e => e.EntryType == OwnerLedgerEntryType.Withholding).Amount);
        Assert.All(lines, e => Assert.Equal((2026, 11, "TEST-IT@2"), (e.PeriodYear, e.PeriodMonth, e.RuleVersion)));

        // The F24 of November is the obligations of November, with the code and the version of the rule.
        var f24 = await world.Withholding.GenerateF24Async(OrgId, 11, 2026);
        Assert.Equal((259.99m, "1919", "TEST-IT@2", TaxFilingState.Generated), (f24.TotalAmount, f24.TaxCode, f24.RuleVersion, f24.FilingState));
    }

    [Fact]
    public async Task OwnerPayout_RetryCannotDuplicateWithholding_Test()
    {
        var world = await PayoutWorld.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m));
        var version = await world.ApprovedVersionAsync(owner, 1000m);
        var command = Payout(owner, version, "k-retry");

        var first = await world.Payouts.ConfirmPayoutAsync(OrgId, command, null);
        var retry = await world.Payouts.ConfirmPayoutAsync(OrgId, command, null);
        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.Payout.Id, retry.Payout.Id);
        Assert.Single(await world.Db.OwnerPayouts.ToListAsync());
        Assert.Single(await world.Db.TaxObligationEvidence.ToListAsync());
        Assert.Equal(2, await world.Db.OwnerLedgerEntries.CountAsync(e => e.SourceType == OwnerLedgerSourceType.OwnerPayout));

        // The same key for other facts is refused, not a second payout.
        var reused = await Assert.ThrowsAsync<DomainConflictException>(() => world.Payouts.ConfirmPayoutAsync(OrgId, command with { GrossAmount = 700m }, null));
        Assert.Equal(OwnerPayoutErrors.PayoutKeyReused, reused.Code);

        // A reversal keeps the decision and adds its opposite: the obligations net to zero, and a second reversal is refused.
        var reversed = await world.Payouts.ReversePayoutAsync(OrgId, first.Payout.Id, "TEST bonifico respinto", null);
        Assert.Equal(OwnerPayoutStatus.Reversed, reversed.Payout.Status);
        Assert.Equal(2, reversed.Evidence.Count);
        Assert.Equal(first.Evidence[0].Id, reversed.Evidence.Single(e => e.ReversesEvidenceId is not null).ReversesEvidenceId);
        Assert.Equal(0m, reversed.Evidence.Sum(e => e.Amount ?? 0m));
        Assert.Equal(0m, (await world.Payouts.ReconcileAsync(OrgId, 2026)).ObligationsTotal);
        Assert.Equal(0m, await world.Db.OwnerLedgerEntries.Where(e => e.SourceType == OwnerLedgerSourceType.OwnerPayout).SumAsync(e => e.Amount));
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Payouts.ReversePayoutAsync(OrgId, first.Payout.Id, "TEST", null));
    }

    [Fact]
    public async Task OwnerPayout_GeneratedTaxFileIsNotMarkedSubmitted_Test()
    {
        var world = await PayoutWorld.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m));
        var version = await world.ApprovedVersionAsync(owner, 1000m);
        await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(owner, version, "k-file"), null);

        var f24 = await world.Withholding.GenerateF24Async(OrgId, 11, 2026);
        Assert.Equal((TaxFilingState.Generated, 210m), (f24.FilingState, f24.TotalAmount));
        Assert.Null(f24.HandoffReference);
        Assert.Null(f24.ReceiptReference);

        // Generated is never submitted: no jump, a handoff for the submission, a receipt for the acceptance.
        var jump = await Assert.ThrowsAsync<DomainConflictException>(
            () => world.Payouts.SetF24FilingStateAsync(OrgId, f24.Id, new TaxFilingCommand { State = TaxFilingState.Submitted, HandoffReference = "x" }, null));
        Assert.Equal(OwnerPayoutErrors.FilingTransitionInvalid, jump.Code);
        await world.Payouts.SetF24FilingStateAsync(OrgId, f24.Id, new TaxFilingCommand { State = TaxFilingState.Reviewed }, "auth0|finance");
        var noHandoff = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Payouts.SetF24FilingStateAsync(OrgId, f24.Id, new TaxFilingCommand { State = TaxFilingState.Submitted }, null));
        Assert.Equal("handoffReference", noHandoff.Extensions["field"]);
        var submitted = await world.Payouts.SetF24FilingStateAsync(
            OrgId, f24.Id, new TaxFilingCommand { State = TaxFilingState.Submitted, HandoffReference = "TEST consegnato al commercialista" }, "auth0|finance");
        Assert.Equal(TaxFilingState.Submitted, submitted.FilingState);
        await Assert.ThrowsAsync<DomainConflictException>(() => world.Withholding.GenerateF24Async(OrgId, 11, 2026));
        await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Payouts.SetF24FilingStateAsync(OrgId, f24.Id, new TaxFilingCommand { State = TaxFilingState.Accepted }, null));
        var accepted = await world.Payouts.SetF24FilingStateAsync(
            OrgId, f24.Id, new TaxFilingCommand { State = TaxFilingState.Accepted, ReceiptReference = "TEST-RICEVUTA-1" }, null);
        Assert.Equal(("TEST-RICEVUTA-1", "TEST consegnato al commercialista"), (accepted.ReceiptReference, accepted.HandoffReference));

        // The annual data starts generated too, and the year reconciles: obligations, F24 and annual data agree.
        var annual = await world.Payouts.GenerateAnnualDatasetAsync(OrgId, 2026, null);
        Assert.Equal((TaxFilingState.Generated, 210m, 1000m, 1), (annual.FilingState, annual.TotalWithheld, annual.TotalGrossBase, annual.EvidenceCount));
        var reconciliation = await world.Payouts.ReconcileAsync(OrgId, 2026);
        Assert.True(reconciliation.Reconciled);
        Assert.Equal((210m, 210m, 210m), (reconciliation.ObligationsTotal, reconciliation.F24Total, reconciliation.AnnualTotal!.Value));
    }

    /// <summary>The F24 of a month is due on the 16th of the next month, and on 16 January of the next year for December (ADR-018-R8).</summary>
    [Fact]
    public async Task OwnerPayout_F24IsDueOnThe16thOfTheNextMonth_Test()
    {
        var world = await PayoutWorld.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m));
        var version = await world.ApprovedVersionAsync(owner, 1000m);
        await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(owner, version, "k-nov"), null);
        await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(owner, version, "k-dec") with { PaidOn = new DateOnly(2026, 12, 20) }, null);

        Assert.Equal(new DateTime(2026, 12, 16, 0, 0, 0, DateTimeKind.Utc), (await world.Withholding.GenerateF24Async(OrgId, 11, 2026)).DueDate);
        Assert.Equal(new DateTime(2027, 1, 16, 0, 0, 0, DateTimeKind.Utc), (await world.Withholding.GenerateF24Async(OrgId, 12, 2026)).DueDate);
    }

    /// <summary>
    /// The Italian forms are built from the evidence of an Italian rule only, in euro, and never for a month whose obligations net to nothing: a Swiss rule that
    /// withholds (whatever its parameters say) is no F24 or CU, a payout reversed in its month leaves no zero F24, and a payout in another currency is not guessed.
    /// </summary>
    [Fact]
    public async Task OwnerPayout_ItalianFormsNeverFileAnotherJurisdictionOrANothingMonth_Test()
    {
        var world = await PayoutWorld.CreateAsync();

        // A Swiss rule set that asks a withholding is still a Swiss decision: no F24, no annual data.
        var swiss = await world.OwnerAsync("CH", "EUR", ItalianRule("1", 21m));
        var swissVersion = await world.ApprovedVersionAsync(swiss, 1000m);
        var swissPayout = await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(swiss, swissVersion, "k-ch"), null);
        Assert.Equal(("CH", TaxDecision.Applicable), (Assert.Single(swissPayout.Evidence).Jurisdiction, swissPayout.Evidence[0].Decision));
        Assert.Equal(OwnerPayoutErrors.NoObligations, (await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.GenerateF24Async(OrgId, 11, 2026))).Code);
        Assert.Equal(OwnerPayoutErrors.NoObligations, (await Assert.ThrowsAsync<DomainRuleException>(() => world.Payouts.GenerateAnnualDatasetAsync(OrgId, 2026, null))).Code);

        // An Italian payout makes the F24 of its month; reversed in that month, its obligations net to nothing and the generated record goes with them.
        var italian = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m));
        var italianVersion = await world.ApprovedVersionAsync(italian, 1000m);
        var payout = await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(italian, italianVersion, "k-it"), null);
        Assert.Equal(210m, (await world.Withholding.GenerateF24Async(OrgId, 11, 2026)).TotalAmount);
        await world.Payouts.ReversePayoutAsync(OrgId, payout.Payout.Id, "TEST bonifico respinto", null);
        Assert.Equal(OwnerPayoutErrors.NoObligations, (await Assert.ThrowsAsync<DomainRuleException>(() => world.Withholding.GenerateF24Async(OrgId, 11, 2026))).Code);
        Assert.Empty(await world.Db.F24Records.ToListAsync());

        // The withholding is declared in euro: a payout in another currency that the rule makes applicable is not guessed into one.
        var franc = await world.OwnerAsync("IT", "CHF", ItalianRule("1", 21m));
        world.Db.OwnerFxRates.Add(new OwnerFxRate
        {
            OrgId = OrgId,
            BaseCurrency = "EUR",
            QuoteCurrency = "CHF",
            RateDate = new DateOnly(2026, 10, 10),
            Rate = 0.95m,
            Source = "TEST BCE",
        });
        await world.Db.SaveChangesAsync();
        var francVersion = await world.ApprovedVersionAsync(franc, 1000m);
        var noEuro = await Assert.ThrowsAsync<DomainRuleException>(() => world.Payouts.ConfirmPayoutAsync(OrgId, Payout(franc, francVersion, "k-chf") with { Currency = "CHF" }, null));
        Assert.Equal((OwnerPayoutErrors.TaxApplicabilityUnresolved, "currency"), (noEuro.Code, noEuro.Extensions["field"]));
    }

    /// <summary>
    /// A transfer is recorded once: a month an owner settled with a confirmed payout is settled by it, so the wire of the same statements is neither posted nor
    /// counted by the close (the payout is the record that carries the tax decision), and nothing is debited twice.
    /// </summary>
    [Fact]
    public async Task OwnerPayout_SettlesTheMonthAndItsWireIsNeitherPostedNorCountedTwice_Test()
    {
        var world = await PayoutWorld.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m));
        var version = await world.ApprovedVersionAsync(owner, 1000m);
        var property = (await world.Db.Properties.SingleAsync()).Id;

        // The October statement was paid by a wire marked paid on 3 November: the ledger of November holds its line.
        await world.PaidWireAsync(owner, property, 1000m);
        await world.Accounting.PostPeriodAsync(OrgId, 2026, 11, null);
        Assert.Equal(-1000m, await world.Db.OwnerLedgerEntries.Where(e => e.SourceType == OwnerLedgerSourceType.WireBatch).SumAsync(e => e.Amount));

        // The payout recorded for the same money settles the month: the wire line is reversed, and the account is debited once.
        await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(owner, version, "k-wire", 1000m) with { AgencyCollected = false }, null);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var posted = await world.Accounting.PostPeriodAsync(OrgId, 2026, 11, null);
        Assert.Equal(1, posted.Reversed);
        var november = await world.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.PeriodMonth == 11).ToListAsync();
        Assert.Equal(-1000m, november.Sum(e => e.Amount));
        Assert.Equal(0m, november.Where(e => e.SourceType == OwnerLedgerSourceType.WireBatch).Sum(e => e.Amount));

        // The close counts the payout once: due 1000, paid 1000.
        var closed = await world.Accounting.CloseAsync(OrgId, owner, 2026, 10, new OwnerCloseCommand(), mayWaive: false, null);
        Assert.Equal((1000m, 1000m, 0m), (closed.DueAmount!.Value, closed.PaidAmount!.Value, closed.Difference!.Value));
    }

    /// <summary>A reversal never posts into a closed month: the payout stays confirmed and nothing is written.</summary>
    [Fact]
    public async Task OwnerPayout_ReversalIsRefusedWhenTheMonthItWouldPostIntoIsClosed_Test()
    {
        var world = await PayoutWorld.CreateAsync();
        var owner = await world.OwnerAsync("IT", "EUR", ItalianRule("1", 21m));
        var version = await world.ApprovedVersionAsync(owner, 1000m);
        var payout = await world.Payouts.ConfirmPayoutAsync(OrgId, Payout(owner, version, "k-closed"), null);

        // The month of the payment (5 November) and the month of today (12 November) are the same, and it is closed.
        var now = world.Clock.GetUtcNow().UtcDateTime;
        world.Db.OwnerClosePeriods.Add(new OwnerClosePeriod
        {
            OrgId = OrgId,
            PropertyOwnerId = owner,
            PeriodYear = 2026,
            PeriodMonth = 11,
            Status = OwnerClosePeriodStatus.Closed,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await world.Db.SaveChangesAsync();

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => world.Payouts.ReversePayoutAsync(OrgId, payout.Payout.Id, "TEST", null));
        Assert.Equal(OwnerAccountingErrors.PeriodClosed, refused.Code);
        world.Db.ChangeTracker.Clear();
        Assert.Equal(OwnerPayoutStatus.Confirmed, (await world.Db.OwnerPayouts.SingleAsync()).Status);
        Assert.Single(await world.Db.TaxObligationEvidence.ToListAsync());
        Assert.Equal(2, await world.Db.OwnerLedgerEntries.CountAsync(e => e.SourceType == OwnerLedgerSourceType.OwnerPayout));
    }

    private static OwnerAccountingRuleParameters ItalianRule(string version, decimal rate) => new()
    {
        WithholdingApplies = true,
        WithholdingRatePercent = rate,
        WithholdingTaxCode = "1919",
        RequiresAgencyCollection = true,
        RequiresQualifyingShortLet = true,
        GrossBaseComponents = ["rent", "cleaning"],
        Rounding = version == "2" ? "half_up" : "half_even",
        RetentionYears = 10,
    };

    private static OwnerAccountingPolicyCommand Policy(string jurisdiction, string version, OwnerAccountingRuleParameters parameters, DateOnly from, DateOnly? to) => new()
    {
        Jurisdiction = jurisdiction,
        RuleCode = "TEST-" + jurisdiction,
        Version = version,
        ValidFrom = from,
        ValidTo = to,
        SourceUrl = "https://example.com/test-rule",
        SourceSha256 = new string('f', 64),
        Parameters = parameters,
    };

    private static OwnerFiscalProfileCommand Profile(string jurisdiction, string currency, Guid policy, DateOnly from) => new()
    {
        ValidFrom = from,
        EntityKind = OwnerEntityKind.NaturalPerson,
        LegalName = "TEST Proprietario " + jurisdiction,
        TaxResidenceCountry = jurisdiction,
        LegalAddress = "Via del Test 1",
        FiscalCode = "RSSMRA70A01H501W",
        SettlementCurrency = currency,
        BankAccountReference = "IT60X0542811101000000123456",
        RevenueRecognitionRule = "cash",
        TaxJurisdiction = jurisdiction,
        AccountingPolicyId = policy,
    };

    /// <summary>A payout of 800 of the October version, paid on 5 November, the agency having collected the rent of a qualifying short let.</summary>
    private static OwnerPayoutCommand Payout(Guid owner, Guid version, string key, decimal gross = 800m) => new()
    {
        IdempotencyKey = key,
        OwnerId = owner,
        StatementVersionId = version,
        GrossAmount = gross,
        Currency = "EUR",
        PaidOn = new DateOnly(2026, 11, 5),
        Reference = "TEST-BONIFICO",
        AgencyCollected = true,
        QualifyingShortLet = true,
        Components = new Dictionary<string, decimal> { ["rent"] = 900m, ["cleaning"] = 100m },
    };

    private sealed class PayoutWorld
    {
        private PayoutWorld(AppDbContext db)
        {
            Db = db;
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
            Fiscal = new OwnerFiscalProfileService(db, Clock, NullLogger<OwnerFiscalProfileService>.Instance);
            Accounting = new OwnerAccountingService(db, Fiscal, Clock, NullLogger<OwnerAccountingService>.Instance);
            Payouts = new OwnerPayoutService(db, Fiscal, Clock, NullLogger<OwnerPayoutService>.Instance);
            Withholding = new WithholdingService(db, Clock);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public OwnerAccountingService Accounting { get; }

        public OwnerPayoutService Payouts { get; }

        public WithholdingService Withholding { get; }

        public static Task<PayoutWorld> CreateAsync() => Task.FromResult(new PayoutWorld(new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"Payouts_{Guid.NewGuid()}").Options,
            new PayoutTenant(),
            new EphemeralDataProtectionProvider())));

        /// <summary>An owner with its approved rule set (its own version per jurisdiction) and a complete profile from January.</summary>
        public async Task<Guid> OwnerAsync(string jurisdiction, string currency, OwnerAccountingRuleParameters parameters, DateOnly? validTo = null)
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = jurisdiction, Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();
            var policy = (await Fiscal.ListPoliciesAsync(OrgId)).FirstOrDefault(p => p.RuleCode == "TEST-" + jurisdiction && p.Version == "1");
            if (policy is null)
            {
                policy = await Fiscal.CreatePolicyAsync(OrgId, Policy(jurisdiction, "1", parameters, new DateOnly(2026, 1, 1), validTo), null);
                await Fiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", null);
            }

            await Fiscal.SaveProfileAsync(OrgId, owner.Id, Profile(jurisdiction, currency, policy.Id, new DateOnly(2026, 1, 1)), null);
            return owner.Id;
        }

        /// <summary>A legacy statement of October for the owner paid by a wire batch marked paid on 3 November.</summary>
        public async Task PaidWireAsync(Guid owner, Guid property, decimal amount)
        {
            var statement = new OwnerStatement
            {
                OrgId = OrgId,
                PropertyOwnerId = owner,
                PropertyId = property,
                Month = 10,
                Year = 2026,
                Currency = "EUR",
                Status = StatementStatus.Published,
                NetPayable = amount,
            };
            var batch = new WireBatch
            {
                OrgId = OrgId,
                Currency = "EUR",
                DebitAccount = "IT60X0542811101000000654321",
                TotalAmount = amount,
                TransferCount = 1,
                Status = WireBatchStatus.Paid,
                PaidAt = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc),
            };
            Db.OwnerStatements.Add(statement);
            Db.WireBatches.Add(batch);
            Db.WireBatchStatements.Add(new WireBatchStatement { OrgId = OrgId, WireBatchId = batch.Id, OwnerStatementId = statement.Id });
            await Db.SaveChangesAsync();
        }

        /// <summary>The approved October version of an owner with one property of its own and one payment.</summary>
        public async Task<Guid> ApprovedVersionAsync(Guid owner, decimal paid)
        {
            var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" };
            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 10, 10),
                CheckOutDate = new DateTime(2026, 10, 12),
                NumberOfGuests = 2,
                Status = BookingStatus.CheckedOut,
            };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                PropertyOwnerId = owner,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2026, 1, 1),
            });
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.Payments.Add(new Payment
            {
                OrgId = OrgId,
                BookingId = booking.Id,
                Amount = paid,
                Status = PaymentStatus.Completed,
                TransactionId = "TEST-" + Guid.NewGuid().ToString("N"),
                Description = "TEST",
                ProcessedAt = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc),
            });

            // A stay of an owner without a mandate holds its version back (E13/F01/US01/T02): the owner has one, of zero (the commission is not the point).
            if (!await Db.OwnerMandates.AnyAsync(m => m.PropertyOwnerId == owner))
                Db.OwnerMandates.Add(new OwnerMandate { OrgId = OrgId, PropertyOwnerId = owner, CommissionPercent = 0m, ValidFrom = new DateOnly(2026, 1, 1) });
            await Db.SaveChangesAsync();

            await Accounting.PostPeriodAsync(OrgId, 2026, 10, null);
            Clock.Advance(TimeSpan.FromMinutes(1));
            var version = await Accounting.GenerateVersionAsync(OrgId, owner, 2026, 10, null);
            await Accounting.ApproveVersionAsync(OrgId, version.Id, null);
            Clock.Advance(TimeSpan.FromMinutes(1));
            return version.Id;
        }
    }
}

file sealed class PayoutTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
