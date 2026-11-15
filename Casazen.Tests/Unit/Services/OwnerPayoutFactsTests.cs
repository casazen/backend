using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E18/F01/US01/T01 (NF-117, AC-NF117-1, NF-110, ⚖ I9, R5, R6): the facts of a payout come from the owner ledger of the version it pays, never from the dialog. A stay
/// the portal already withheld is never withheld again by the agency, the tourist tax is never in the base, the seeded Italian rule is a draft that decides nothing
/// until the accountant approves it, an owner paid in francs gets the withholding in euro at the rate of the payout day, and a payout never pays more than the
/// version still owes. The rule of the tests is the committed one (<c>OwnerAccounting:ItalianShortLetRule</c> of <c>appsettings.json</c>): 21%, 1919, <c>stay_gross</c>.
/// </summary>
public class OwnerPayoutFactsTests
{
    /// <summary>The day of the payouts: 5 November, for the statement of October.</summary>
    private static readonly DateOnly PaidOn = new(2026, 11, 5);

    /// <summary>A hash of a source document the accountant checked (invented).</summary>
    private static readonly string CheckedSource = new('c', 64);

    /// <summary>
    /// Money (AC-NF117-1, ⚖ R5): a stay of a portal that withholds for an Italian private host itself (Airbnb, the agency holding the account: the portal collects and
    /// pays the agency) and a stay whose payment carries the withholding of its portal are left out of the base; only the stay the agency collected is withheld. A
    /// payout whose stays the portals all withheld withholds nothing.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_PortalAlreadyWithheld_NoDoubleWithholding_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        await world.AgencyHoldsAccountAsync(house, BookingSource.Airbnb);
        var airbnb = await world.StayAsync(house, BookingSource.Airbnb, 500m);
        var withheldOnPayment = await world.StayAsync(house, BookingSource.BookingCom, 300m, paid: true, portalWithholding: 63m);
        var direct = await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);

        var result = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-portal"), null);

        // The base is the stay the agency collected: 21% of 1000, once; the payout settles the whole balance (500 + 300 + 1000).
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal((TaxDecision.Applicable, 1000m, 210m, "1919"), (evidence.Decision, evidence.GrossBase!.Value, evidence.Amount!.Value, evidence.TaxCode));
        Assert.Equal((1800m, 210m, 1590m), (result.Payout.GrossAmount, result.Payout.WithholdingAmount, result.Payout.NetAmount));

        var facts = TaxObligationEvidenceDto.FactsOf(evidence.FactsJson)!;
        Assert.Equal(PayoutFactRules.FromLedger, facts.Source);
        Assert.Equal(1000m, facts.Components[PayoutFactRules.StayGross]);
        var ofAirbnb = Assert.Single(facts.Stays, s => s.BookingId == airbnb);
        Assert.Equal((true, PayoutStayReasons.WithheldByPortalRule, OwnerWithholdingRules.AlreadyWithheldByPortal, false), (ofAirbnb.WithheldByPortal, ofAirbnb.PortalWithholding, ofAirbnb.Reason, ofAirbnb.InBase));
        Assert.Equal((true, PayoutStayReasons.PortalPaysAgency), (ofAirbnb.CollectedByAgency, ofAirbnb.Collection));
        var ofPayment = Assert.Single(facts.Stays, s => s.BookingId == withheldOnPayment);
        Assert.Equal((true, PayoutStayReasons.WithheldOnPayment, false), (ofPayment.WithheldByPortal, ofPayment.PortalWithholding, ofPayment.InBase));
        var ofDirect = Assert.Single(facts.Stays, s => s.BookingId == direct);
        Assert.Equal((true, 1000m, PayoutStayReasons.InBase, PayoutStayReasons.RecordedPayment), (ofDirect.InBase, ofDirect.BaseAmount!.Value, ofDirect.Reason, ofDirect.Collection));
        Assert.Equal(210m, (await world.Withholding.GenerateF24Async(world.OrgId, 11, 2026)).TotalAmount);

        // An owner whose only stay the portal withheld: no withholding at all, the gross paid whole, and the evidence says why.
        var other = await world.OwnerAsync();
        var otherHouse = await world.PropertyAsync(other);
        await world.AgencyHoldsAccountAsync(otherHouse, BookingSource.Airbnb);
        await world.StayAsync(otherHouse, BookingSource.Airbnb, 400m);
        var otherVersion = await world.ApprovedVersionAsync(other);
        var none = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(other, otherVersion, "k-airbnb"), null);
        Assert.Equal((TaxDecision.NotApplicable, OwnerWithholdingRules.AlreadyWithheldByPortal), (none.Evidence[0].Decision, none.Evidence[0].ReasonCode));
        Assert.Null(none.Evidence[0].Amount);
        Assert.Equal((400m, 0m, 400m), (none.Payout.GrossAmount, none.Payout.WithholdingAmount, none.Payout.NetAmount));
    }

    /// <summary>
    /// Money: the tourist tax is never in the base. A stay paid with a payment the agency recorded (1020, of which 20 tourist tax) and a stay of a portal that pays the
    /// agency (520, of which 20 tourist tax) give a base of 1000 + 500: the gross without the tax, as the ledger posted them.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_TouristTaxExcludedFromBase_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        await world.AgencyHoldsAccountAsync(house, BookingSource.BookingCom);
        var paid = await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true, touristTax: 20m);
        var portal = await world.StayAsync(house, BookingSource.BookingCom, 500m, touristTax: 20m);
        var version = await world.ApprovedVersionAsync(owner);

        var result = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-tax"), null);

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal((1500m, 315m), (evidence.GrossBase!.Value, evidence.Amount!.Value));
        var facts = TaxObligationEvidenceDto.FactsOf(evidence.FactsJson)!;
        Assert.Equal((1000m, 20m, true), Of(facts, paid));
        Assert.Equal((500m, 20m, true), Of(facts, portal));
        Assert.Equal(1500m, facts.Components[PayoutFactRules.StayGross]);

        static (decimal Gross, decimal TouristTax, bool InBase) Of(PayoutFacts facts, Guid stay)
        {
            var fact = Assert.Single(facts.Stays, s => s.BookingId == stay);
            return (fact.Gross, fact.TouristTax, fact.InBase);
        }
    }

    /// <summary>
    /// Money (⚖ I9): the Italian rule of the configuration is seeded once as a draft that is not approved (21%, 1919, the gross without the tourist tax, half up to the
    /// cent, Airbnb withholds itself, the source of the tax agency not pinned). An owner whose rule in force is the draft is never withheld on: the payout is refused
    /// and nothing is written, the preview says why. The draft is approved only with the hash of the document the accountant checked; then it decides.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_DraftRuleNeverApplies_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);

        var draft = await world.Fiscal.EnsureItalianShortLetDraftAsync(world.OrgId);
        Assert.NotNull(draft);
        Assert.Null(await world.Fiscal.EnsureItalianShortLetDraftAsync(world.OrgId));
        Assert.Null(draft!.ApprovedAt);
        Assert.Equal(("IT", "IT-STR-WITHHOLDING", string.Empty), (draft.Jurisdiction, draft.RuleCode, draft.SourceSha256));
        Assert.StartsWith("https://www.agenziaentrate.gov.it/", draft.SourceUrl, StringComparison.Ordinal);
        var parameters = OwnerAccountingRuleParameters.FromJson(draft.ParametersJson)!;
        Assert.Equal((true, 21m, "1919", "half_up"), (parameters.WithholdingApplies, parameters.WithholdingRatePercent!.Value, parameters.WithholdingTaxCode, parameters.Rounding));
        Assert.Equal([PayoutFactRules.StayGross], parameters.GrossBaseComponents);
        Assert.Equal(["Airbnb"], parameters.PortalsWithholding!);

        // From 1 November the owner follows the draft: the payout of 5 November is refused, nothing is written.
        await world.Fiscal.SaveProfileAsync(world.OrgId, owner, FactsWorld.Profile("EUR", draft.Id, new DateOnly(2026, 11, 1)), null);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-draft"), null));
        Assert.Equal(OwnerFiscalErrors.PolicyNotApproved, refused.Code);
        world.Db.ChangeTracker.Clear();
        Assert.Empty(await world.Db.OwnerPayouts.ToListAsync());
        Assert.Empty(await world.Db.TaxObligationEvidence.ToListAsync());
        Assert.Empty(await world.Db.OwnerLedgerEntries.Where(e => e.SourceType == OwnerLedgerSourceType.OwnerPayout).ToListAsync());

        var preview = await world.Payouts.PreviewPayoutAsync(world.OrgId, version, PaidOn);
        Assert.Equal((OwnerFiscalErrors.PolicyNotApproved, (WithholdingDecision?)null, (decimal?)null), (preview.BlockingCode, preview.Decision, preview.WithholdingAmount));
        Assert.Equal(1000m, Assert.Single(preview.Facts.Stays).Gross);

        // The draft's source is not pinned: no approval without the hash of the document checked.
        var unpinned = await Assert.ThrowsAsync<DomainRuleException>(() => world.Fiscal.ApprovePolicyAsync(world.OrgId, draft.Id, "TEST Commercialista", null));
        Assert.Equal((OwnerFiscalErrors.PolicyInvalid, "sourceSha256"), (unpinned.Code, unpinned.Extensions["field"]));
        world.Db.ChangeTracker.Clear();
        Assert.Null((await world.Db.OwnerAccountingPolicies.SingleAsync(p => p.Id == draft.Id)).ApprovedAt);

        var approved = await world.Fiscal.ApprovePolicyAsync(world.OrgId, draft.Id, "TEST Commercialista", null, CheckedSource);
        Assert.Equal(CheckedSource, approved.SourceSha256);
        var paid = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-draft"), null);
        var decision = Assert.Single(paid.Evidence);
        Assert.Equal((TaxDecision.Applicable, 210m, "IT-STR-WITHHOLDING", "half_up"), (decision.Decision, decision.Amount!.Value, decision.RuleCode, decision.Rounding));
    }

    /// <summary>
    /// Money (⚖ R6): an owner of an Italian property settled in francs gets the Italian withholding computed in euro (the base of the stay as it was paid, 1000 EUR,
    /// 21% = 210 EUR, the evidence and the F24 in euro) and kept from the payout at the agency's rate of the payout day (210 × 0.94 = 197.40 CHF); the net is in francs.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_ChfOwnerWithholdingInEur_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync("CHF");
        await world.RateAsync("EUR", "CHF", new DateOnly(2026, 10, 10), 0.95m);
        await world.RateAsync("EUR", "CHF", new DateOnly(2026, 11, 4), 0.94m);
        var house = await world.PropertyAsync(owner);
        await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);

        var result = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-chf") with { Currency = "CHF" }, null);

        // The statement owes 950 CHF (1000 EUR at the rate of the payment day); the withholding is 210 EUR, kept as 197.40 CHF.
        Assert.Equal(("CHF", 950m, 197.40m, 752.60m), (result.Payout.Currency, result.Payout.GrossAmount, result.Payout.WithholdingAmount, result.Payout.NetAmount));
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal((1000m, 210m), (evidence.GrossBase!.Value, evidence.Amount!.Value));
        var facts = TaxObligationEvidenceDto.FactsOf(evidence.FactsJson)!;
        Assert.Equal(("CHF", "EUR"), (facts.Currency, facts.WithholdingCurrency));
        Assert.Equal(new PayoutFx("EUR", "CHF", 0.94m, "TEST BCE", new DateOnly(2026, 11, 4)), facts.Fx);

        // The ledger line of the withholding says it in francs, from the euro decided and the rate of the day.
        var line = await world.Db.OwnerLedgerEntries.AsNoTracking().SingleAsync(e => e.EntryType == OwnerLedgerEntryType.Withholding);
        Assert.Equal(("CHF", -197.40m, "EUR", -210m, 0.94m), (line.StatementCurrency, line.Amount, line.OriginalCurrency, line.OriginalAmount, line.FxRate));
        Assert.Equal(210m, (await world.Withholding.GenerateF24Async(world.OrgId, 11, 2026)).TotalAmount);
    }

    /// <summary>
    /// Money (guards): a payout never pays more than the version still owes (422 with the balance); a payout of a part of it decides on all its stays, and a second
    /// payout of the same version decides on none of them again (no double withholding); once the balance is paid nothing more is.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_GrossNeverExceedsTheBalance_AndAStayIsDecidedOnce_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);

        var tooMuch = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-much") with { GrossAmount = 1000.01m }, null));
        Assert.Equal((OwnerPayoutErrors.PayoutExceedsBalance, 1000m), (tooMuch.Code, (decimal)tooMuch.Extensions["balance"]!));

        var first = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-first") with { GrossAmount = 600m }, null);
        Assert.Equal((210m, 390m), (first.Payout.WithholdingAmount, first.Payout.NetAmount));

        var second = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-second"), null);
        Assert.Equal((400m, 0m, 400m), (second.Payout.GrossAmount, second.Payout.WithholdingAmount, second.Payout.NetAmount));
        Assert.Equal(OwnerWithholdingRules.NoStaysToDecide, Assert.Single(second.Evidence).ReasonCode);

        var nothingLeft = await Assert.ThrowsAsync<DomainRuleException>(() => world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-third"), null));
        Assert.Equal(OwnerPayoutErrors.PayoutExceedsBalance, nothingLeft.Code);
        Assert.Equal(210m, await world.Db.TaxObligationEvidence.SumAsync(e => e.Amount ?? 0m));
    }

    /// <summary>
    /// Money: a stay paid in two months (a deposit of 400 in October, the balance of 600 in November) is withheld once on each part, by the payout that pays it: the
    /// payout of October decides the deposit (84), the payout of November only the balance (126), never the deposit again and never without the balance.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_StayPaidInTwoMonths_EachPartIsDecidedOnce_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        var guest = new Guest { OrgId = world.OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = world.OrgId,
            PropertyId = house,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 30),
            CheckOutDate = new DateTime(2026, 11, 2),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Manual,
            BasePrice = 1000m,
            TotalPrice = 1000m,
        };
        world.Db.Guests.Add(guest);
        world.Db.Bookings.Add(booking);
        foreach (var (amount, day) in new[] { (400m, new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc)), (600m, new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc)) })
        {
            world.Db.Payments.Add(new Payment
            {
                OrgId = world.OrgId,
                BookingId = booking.Id,
                Amount = amount,
                Status = PaymentStatus.Completed,
                TransactionId = "TEST-" + Guid.NewGuid().ToString("N"),
                Description = "TEST",
                ProcessedAt = day,
            });
        }

        await world.Db.SaveChangesAsync();
        var october = await world.ApprovedVersionAsync(owner);
        var deposit = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, october, "k-october"), null);
        Assert.Equal((400m, 84m), (deposit.Evidence[0].GrossBase!.Value, deposit.Evidence[0].Amount!.Value));

        var november = await world.ApprovedVersionAsync(owner, 11);
        var balance = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, november, "k-november") with { PaidOn = new DateOnly(2026, 12, 4) }, null);
        Assert.Equal(OwnerWithholdingRules.Applicable, balance.Evidence[0].ReasonCode);
        Assert.Equal((600m, 600m, 126m), (balance.Payout.GrossAmount, balance.Evidence[0].GrossBase!.Value, balance.Evidence[0].Amount!.Value));
        Assert.Equal(booking.Id, Assert.Single(TaxObligationEvidenceDto.FactsOf(balance.Evidence[0].FactsJson)!.Stays).BookingId);
        Assert.Equal(210m, await world.Db.TaxObligationEvidence.SumAsync(e => e.Amount ?? 0m));
    }

    /// <summary>
    /// Money (review of S029): a stay is withheld once, never lost and never twice, when the payout that withheld it is reversed after another payout of the same
    /// version. A qualifying stay of 1000: P1 of 600 withholds 210, P2 of 400 decides nothing; P1 is reversed (its evidence −210) while P2 stays; P3 pays the 600
    /// left and withholds the 210 again: 210 on 1000 in all, never 0 and never 420.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_PayoutReversedAfterAPartialOne_IsWithheldAgainOnce_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        var stay = await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);

        var first = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-p1") with { GrossAmount = 600m }, null);
        Assert.Equal((210m, 390m), (first.Payout.WithholdingAmount, first.Payout.NetAmount));
        var second = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-p2") with { GrossAmount = 400m }, null);
        Assert.Equal((0m, OwnerWithholdingRules.NoStaysToDecide), (second.Payout.WithholdingAmount, Assert.Single(second.Evidence).ReasonCode));

        // The wire of P1 bounced: P1 is reversed, P2 is not. The stay P1 decided is open again: the preview and the next payout decide it once more.
        await world.Payouts.ReversePayoutAsync(world.OrgId, first.Payout.Id, "TEST bonifico respinto", null);
        var preview = await world.Payouts.PreviewPayoutAsync(world.OrgId, version, PaidOn);
        Assert.Equal((600m, 210m, (string?)null), (preview.Balance, preview.WithholdingAmount!.Value, preview.BlockingCode));

        var third = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-p3"), null);
        Assert.Equal((600m, 210m, 390m), (third.Payout.GrossAmount, third.Payout.WithholdingAmount, third.Payout.NetAmount));
        var decision = Assert.Single(third.Evidence);
        Assert.Equal((TaxDecision.Applicable, 1000m, 210m), (decision.Decision, decision.GrossBase!.Value, decision.Amount!.Value));
        Assert.Equal(stay, Assert.Single(TaxObligationEvidenceDto.FactsOf(decision.FactsJson)!.Stays).BookingId);

        // 210 on 1000 in all (P1, its reversal and P3; P2 decided nothing), the F24 of November says the same, and nothing is left to pay or to decide.
        Assert.Equal(210m, await world.Db.TaxObligationEvidence.SumAsync(e => e.Amount ?? 0m));
        Assert.Equal(210m, (await world.Withholding.GenerateF24Async(world.OrgId, 11, 2026)).TotalAmount);
        var nothingLeft = await Assert.ThrowsAsync<DomainRuleException>(() => world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-p4"), null));
        Assert.Equal(OwnerPayoutErrors.PayoutExceedsBalance, nothingLeft.Code);
    }

    /// <summary>
    /// Money (review of S029): the stay of a house deleted after its check-out (nothing to come, so the delete is allowed) is still judged and withheld by the payout of
    /// its statement, and the preview says the same: its regime is read with the deleted property, as the facts are, never a 422 that would block the payout of the
    /// whole version for good.
    /// </summary>
    [Fact]
    public async Task PayoutFacts_PropertyDeletedAfterCheckOut_IsStillDecided_Test()
    {
        var world = new FactsWorld();
        var owner = await world.OwnerAsync();
        var house = await world.PropertyAsync(owner);
        var stay = await world.StayAsync(house, BookingSource.Manual, 1000m, paid: true);
        var version = await world.ApprovedVersionAsync(owner);
        var property = await world.Db.Properties.SingleAsync(p => p.Id == house);
        property.IsDeleted = true;
        property.DeletedAt = world.Clock.GetUtcNow().UtcDateTime;
        await world.Db.SaveChangesAsync();
        world.Db.ChangeTracker.Clear();

        var preview = await world.Payouts.PreviewPayoutAsync(world.OrgId, version, PaidOn);
        Assert.Equal(((string?)null, 210m), (preview.BlockingCode, preview.WithholdingAmount!.Value));

        var result = await world.Payouts.ConfirmPayoutAsync(world.OrgId, world.Payout(owner, version, "k-deleted"), null);
        var decision = Assert.Single(result.Evidence);
        Assert.Equal((TaxDecision.Applicable, 1000m, 210m), (decision.Decision, decision.GrossBase!.Value, decision.Amount!.Value));
        var fact = Assert.Single(TaxObligationEvidenceDto.FactsOf(decision.FactsJson)!.Stays);
        Assert.Equal((stay, true, PayoutStayReasons.InBase), (fact.BookingId, fact.Qualifying, fact.Reason));
    }

    /// <summary>The rules of a stay, pure: a stay longer than the short let, or of a property outside Italy, is no qualifying short let; a stay paid at the property is not the agency's.</summary>
    [Fact]
    public void PayoutFactRules_EvaluateStay_QualifyingAndCollection_Test()
    {
        var settings = new PayoutFactSettings(30, PayoutFactRules.PortalsOf(["Airbnb"]), true, true);
        var treatment = new StayShortLetTreatment(Guid.NewGuid(), true, ShortLetTreatmentReasons.ShortLet, false, false, null, null);
        var stay = new PayoutStayInput(
            Guid.NewGuid(), Guid.NewGuid(), BookingSource.BookingCom, true, true, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 100m, "EUR", 900m, 0m,
            false, false, false, PortalAccountHolder.Agency, false, true, treatment);

        var atProperty = PayoutFactRules.EvaluateStay(stay, settings);
        Assert.Equal((true, false, PayoutStayReasons.CollectedAtProperty, OwnerWithholdingRules.NoCollectionOrIntervention), (atProperty.Qualifying, atProperty.CollectedByAgency, atProperty.Collection, atProperty.Reason));

        var tooLong = PayoutFactRules.EvaluateStay(stay with { CheckOut = new DateOnly(2026, 11, 1), PortalCollects = true }, settings);
        Assert.Equal((false, PayoutStayReasons.StayLengthOutOfRange, OwnerWithholdingRules.NotQualifyingShortLet), (tooLong.Qualifying, tooLong.QualifyingReason, tooLong.Reason));

        var swiss = PayoutFactRules.EvaluateStay(stay with { ItalianProperty = false, PortalCollects = true }, settings);
        Assert.Equal((false, PayoutStayReasons.PropertyNotItalian), (swiss.Qualifying, swiss.QualifyingReason));

        var company = PayoutFactRules.EvaluateStay(stay with { OwnerPrivate = false, PortalCollects = true }, settings);
        Assert.Equal((false, PayoutStayReasons.OwnerNotPrivate), (company.Qualifying, company.QualifyingReason));

        var inBase = PayoutFactRules.EvaluateStay(stay with { PortalCollects = true }, settings);
        Assert.Equal((true, PayoutStayReasons.PortalPaysAgency, PayoutStayReasons.InBase), (inBase.CollectedByAgency, inBase.Collection, inBase.Reason));
    }

    /// <summary>
    /// The world of the facts: an org in an in-memory database, the clock of 12 November 2026, the committed configuration of the owner accounting, the real services
    /// of the ledger, the checks of the owners and the payouts. Everything is invented (TEST owners, @example.com). The tests of the F24 export (E18/F01/US01/T02) use
    /// it too.
    /// </summary>
    internal sealed class FactsWorld
    {
        public FactsWorld()
        {
            Db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"PayoutFacts_{Guid.NewGuid()}").Options,
                new FactsTenant(),
                new EphemeralDataProtectionProvider());
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
            var accounting = OptionsFactory.Create(Committed());
            var fiscalRules = OptionsFactory.Create(new ShortStayFiscalOptions());
            Fiscal = new OwnerFiscalProfileService(Db, Clock, NullLogger<OwnerFiscalProfileService>.Instance, accounting);
            Accounting = new OwnerAccountingService(Db, Fiscal, Clock, NullLogger<OwnerAccountingService>.Instance, accounting);
            var checks = new OwnerTaxChecks(
                Db, new InternalActivityService(Db, NullLogger<InternalActivityService>.Instance, Clock), new TestFeatureFlags(), fiscalRules, Clock, NullLogger<OwnerTaxChecks>.Instance);
            Facts = new OwnerPayoutFacts(Db, checks, new PortalAccountHolderResolver(Db), fiscalRules, accounting);
            Payouts = new OwnerPayoutService(Db, Fiscal, Clock, NullLogger<OwnerPayoutService>.Instance, Facts);
            Withholding = new WithholdingService(Db, Clock);
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public OwnerAccountingService Accounting { get; }

        public OwnerPayoutFacts Facts { get; }

        public OwnerPayoutService Payouts { get; }

        public WithholdingService Withholding { get; }

        /// <summary>The section <c>OwnerAccounting</c> of the committed <c>appsettings.json</c>: the draft and the defaults of the tests are the ones deployed.</summary>
        public static OwnerAccountingOptions Committed()
        {
            var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
                directory = directory.Parent;
            var settings = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(directory!.FullName, "Casazen.Web", "appsettings.json"), optional: false)
                .Build();
            return settings.GetSection(OwnerAccountingOptions.SectionName).Get<OwnerAccountingOptions>()!;
        }

        public static OwnerFiscalProfileCommand Profile(string currency, Guid policy, DateOnly from) => new()
        {
            ValidFrom = from,
            EntityKind = OwnerEntityKind.NaturalPerson,
            LegalName = "TEST Proprietario",
            TaxResidenceCountry = "IT",
            LegalAddress = "Via del Test 1",
            FiscalCode = "RSSMRA70A01H501W",
            SettlementCurrency = currency,
            RevenueRecognitionRule = "cash",
            TaxJurisdiction = "IT",
            AccountingPolicyId = policy,
        };

        /// <summary>A private owner with a mandate of 0% and a profile from January on the committed Italian rule, approved (pinned) unless asked otherwise.</summary>
        public async Task<Guid> OwnerAsync(string currency = "EUR")
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            Db.OwnerMandates.Add(new OwnerMandate { OrgId = OrgId, PropertyOwnerId = owner.Id, CommissionPercent = 0m, ValidFrom = new DateOnly(2026, 1, 1) });
            await Db.SaveChangesAsync();

            var template = Committed().ItalianShortLetRule;
            var rule = (await Fiscal.ListPoliciesAsync(OrgId)).FirstOrDefault(p => p.RuleCode == "TEST-IT");
            if (rule is null)
            {
                rule = await Fiscal.CreatePolicyAsync(
                    OrgId,
                    new OwnerAccountingPolicyCommand
                    {
                        Jurisdiction = "IT",
                        RuleCode = "TEST-IT",
                        Version = "1",
                        ValidFrom = new DateOnly(2026, 1, 1),
                        SourceUrl = template.SourceUrl,
                        SourceSha256 = CheckedSource,
                        Parameters = template.ToParameters(),
                    },
                    null);
                await Fiscal.ApprovePolicyAsync(OrgId, rule.Id, "TEST Commercialista", null);
            }

            await Fiscal.SaveProfileAsync(OrgId, owner.Id, Profile(currency, rule.Id, new DateOnly(2026, 1, 1)), null);
            return owner.Id;
        }

        /// <summary>A house in Italy, in euro, all of <paramref name="owner"/>'s.</summary>
        public async Task<Guid> PropertyAsync(Guid owner)
        {
            var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = "Como", Country = "IT", Currency = "EUR" };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                PropertyOwnerId = owner,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2026, 1, 1),
            });
            await Db.SaveChangesAsync();
            return property.Id;
        }

        /// <summary>The agency holds the account of the house on <paramref name="channel"/> (V2).</summary>
        public async Task AgencyHoldsAccountAsync(Guid property, BookingSource channel)
        {
            Db.PropertyChannelAccountHolders.Add(new PropertyChannelAccountHolder
            {
                OrgId = OrgId,
                PropertyId = property,
                Channel = channel.ToString(),
                Holder = PortalAccountHolder.Agency,
                UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            });
            await Db.SaveChangesAsync();
        }

        /// <summary>The agency's rate of a day: one <paramref name="from"/> is <paramref name="rate"/> <paramref name="to"/>.</summary>
        public async Task RateAsync(string from, string to, DateOnly day, decimal rate)
        {
            Db.OwnerFxRates.Add(new OwnerFxRate { OrgId = OrgId, BaseCurrency = from, QuoteCurrency = to, RateDate = day, Rate = rate, Source = "TEST BCE" });
            await Db.SaveChangesAsync();
        }

        /// <summary>
        /// A checked-out stay of 2 nights in October worth <paramref name="gross"/> without the tourist tax: paid with a payment the agency recorded (the payment of the
        /// whole, tourist tax included, and the withholding of its portal when given), or posted from the booking (a portal that collects, with no commission of its own).
        /// </summary>
        public async Task<Guid> StayAsync(Guid property, BookingSource channel, decimal gross, bool paid = false, decimal touristTax = 0m, decimal portalWithholding = 0m)
        {
            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var portal = FiscalCopy.IsOtaBookingSource(channel);
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 10, 10),
                CheckOutDate = new DateTime(2026, 10, 12),
                NumberOfGuests = 2,
                Status = BookingStatus.CheckedOut,
                Source = channel,
                BasePrice = gross,
                TouristTax = touristTax,
                TotalPrice = gross + touristTax,
                OtaCommission = portal ? 0m : null,
                OtaCompanyCollect = portal ? true : null,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            if (paid)
            {
                Db.Payments.Add(new Payment
                {
                    OrgId = OrgId,
                    BookingId = booking.Id,
                    Amount = gross + touristTax,
                    Status = PaymentStatus.Completed,
                    TransactionId = "TEST-" + Guid.NewGuid().ToString("N"),
                    Description = "TEST",
                    ProcessedAt = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc),
                    OtaWithholdingTax = portalWithholding,
                    WithholdingTaxApplied = portalWithholding > 0m,
                });
            }

            await Db.SaveChangesAsync();
            return booking.Id;
        }

        /// <summary>The ledger of the month of 2026 posted (October unless said), the version of the owner generated and approved.</summary>
        public async Task<Guid> ApprovedVersionAsync(Guid owner, int month = 10)
        {
            await Accounting.PostPeriodAsync(OrgId, 2026, month, null);
            Clock.Advance(TimeSpan.FromMinutes(1));
            var version = await Accounting.GenerateVersionAsync(OrgId, owner, 2026, month, null);
            await Accounting.ApproveVersionAsync(OrgId, version.Id, null);
            Clock.Advance(TimeSpan.FromMinutes(1));
            return version.Id;
        }

        /// <summary>The payout request of the dialog: the version, the day, the reference and the key; no fact, no amount.</summary>
        public OwnerPayoutCommand Payout(Guid owner, Guid version, string key) => new()
        {
            IdempotencyKey = key,
            OwnerId = owner,
            StatementVersionId = version,
            PaidOn = PaidOn,
            Reference = "TEST-BONIFICO",
        };
    }
}

file sealed class FactsTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
