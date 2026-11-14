using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E13/F01/US01/T02 (NF-110, NF-115, fatturazione/analisi-tecnica.md §5bis): the posting of a month covers every booking flow (portal, the agency's own
/// site, the CasaZen showcase, a booking entered by hand), keeps the tourist tax out of the revenue and of the commission base, posts the channel commission
/// with its source, the flat fees of the mandate once per stay, the card fees only when the mandate passes them through, and never a commission of zero.
/// </summary>
public class OwnerLedgerPostingTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly HostScope OrgWide = new(OrgId);

    /// <summary>
    /// Money: a portal stay posts its gross without the tourist tax and its channel commission on the check-out day; a stay of the agency's own site posts on
    /// the day of the payment the agency recorded, or on the check-out when none was recorded.
    /// </summary>
    [Fact]
    public async Task Ledger_PortalStay_PostsGrossAndChannelFeeAtCheckOut_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.MandateAsync(owner, 20m);

        var portal = await world.StayAsync(property, BookingSource.Airbnb, 500m, cleaning: 50m, touristTax: 24m, Day(10, 10), Day(10, 13), otaCommission: 77.50m);
        var paidOutside = await world.StayAsync(property, BookingSource.Direct, 300m, cleaning: 0m, touristTax: 0m, Day(10, 14), Day(10, 20));
        await world.PaymentAsync(paidOutside, 300m, new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), method: PaymentMethod.BankTransfer);
        var manual = await world.StayAsync(property, BookingSource.Manual, 200m, cleaning: 0m, touristTax: 0m, Day(10, 22), Day(10, 25));
        await world.StayAsync(property, BookingSource.Airbnb, 999m, cleaning: 0m, touristTax: 0m, Day(10, 30), Day(11, 2), otaCommission: 99m);

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, "auth0|admin");
        Assert.DoesNotContain(result.Issues, i => i.Blocking);

        var lines = await world.LiveAsync();
        var portalRevenue = Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceId == portal);
        Assert.Equal(
            (OwnerLedgerSourceType.Booking, 500m, new DateOnly(2026, 10, 13), (DateOnly?)null, "CHF"),
            (portalRevenue.SourceType, portalRevenue.Amount, portalRevenue.RecognitionDate, portalRevenue.PaymentDate, portalRevenue.StatementCurrency));
        var fee = Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
        Assert.Equal((portal, -77.50m, new DateOnly(2026, 10, 13), ChannelFeeSources.Payload), (fee.SourceId!.Value, fee.Amount, fee.RecognitionDate, fee.RuleVersion));
        Assert.Equal(-100m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.AgencyCommission && e.SourceId == portal).Amount);

        // The agency's own site: the payment it recorded is the day of the revenue; without one, the check-out (⚖ R1).
        var recorded = Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceType == OwnerLedgerSourceType.Payment);
        Assert.Equal((300m, new DateOnly(2026, 10, 5)), (recorded.Amount, recorded.RecognitionDate));
        Assert.DoesNotContain(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceId == paidOutside);
        var byHand = Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceId == manual);
        Assert.Equal((200m, new DateOnly(2026, 10, 25)), (byHand.Amount, byHand.RecognitionDate));

        // The stay that checks out in November is November's.
        Assert.Equal(1000m, lines.Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).Sum(e => e.Amount));
    }

    /// <summary>
    /// Money: the tourist tax a payment collected is taken out in proportion (a deposit, a balance, a partial refund each lose their own part), so neither the
    /// revenue nor the commission base ever holds it; the gross of a portal stay and the base of its channel commission are without it too.
    /// </summary>
    [Fact]
    public async Task Ledger_TouristTaxNeverInRevenueOrCommissionBase_Test()
    {
        Assert.Equal(100m, OwnerLedgerRules.RevenueOfPayment(104m, 12m, 312m));
        Assert.Equal(150m, OwnerLedgerRules.RevenueOfPayment(156m, 12m, 312m));
        Assert.Equal(80m, OwnerLedgerRules.RevenueOfPayment(80m, 0m, 80m));

        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.MandateAsync(owner, 20m);

        // A stay of the CasaZen showcase: 300 of stay and 12 of tourist tax, paid as a deposit of a third and the balance.
        var showcase = await world.StayAsync(property, BookingSource.Direct, 300m, cleaning: 40m, touristTax: 12m, Day(10, 10), Day(10, 13));
        await world.PaymentAsync(showcase, 104m, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        var balance = await world.PaymentAsync(showcase, 208m, new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc));

        // A portal stay with its tourist tax and no commission in the reservation: the default of Booking.com (15%) on the gross without the tax.
        var portal = await world.StayAsync(property, BookingSource.BookingCom, 400m, cleaning: 0m, touristTax: 16m, Day(10, 20), Day(10, 22));

        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var lines = await world.LiveAsync();
        Assert.Equal([100m, 200m], lines.Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceType == OwnerLedgerSourceType.Payment).Select(e => e.Amount).Order());
        Assert.Equal([-40m, -20m], lines.Where(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission && e.SourceType == OwnerLedgerSourceType.Payment).Select(e => e.Amount).Order());
        Assert.Equal(400m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceId == portal).Amount);
        Assert.Equal(-80m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.AgencyCommission && e.SourceId == portal).Amount);
        var fee = Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
        Assert.Equal((-60m, ChannelFeeSources.Default), (fee.Amount, fee.RuleVersion));

        // A partial refund of the balance: its revenue loses its own part of the tax, never more, never less.
        await world.RefundAsync(balance, 52m);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var after = await world.LiveAsync();
        Assert.Equal([100m, 150m], after.Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceType == OwnerLedgerSourceType.Payment).Select(e => e.Amount).Order());
        Assert.Equal([-30m, -20m], after.Where(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission && e.SourceType == OwnerLedgerSourceType.Payment).Select(e => e.Amount).Order());

        // A base without the cleaning: the share of the cleaning of the stay is out of it as well.
        Assert.Equal(260m, OwnerLedgerRules.CommissionBaseOf(CommissionBase.GrossExclTouristTaxAndCleaning, 300m, 300m, 40m));
        Assert.Equal(300m, OwnerLedgerRules.CommissionBaseOf(CommissionBase.GrossExclTouristTax, 300m, 300m, 40m));
    }

    /// <summary>
    /// Money: the commission of the reservation wins; without one, the rule of the agency in force at the check-out, else the configured default, each line
    /// saying which; a portal with none of them is held back, never posted with a commission of zero.
    /// </summary>
    [Fact]
    public async Task Ledger_ChannelRuleOnlyWhenPayloadHasNoCommission_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.MandateAsync(owner, 0m);
        var rules = await world.Mandates.SaveChannelRulesAsync(
            OrgId, [new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 16m, ValidFrom = new DateOnly(2026, 1, 1) }], null);
        var rule = Assert.Single(rules.Rules);

        var withPayload = await world.StayAsync(property, BookingSource.BookingCom, 300m, 0m, 0m, Day(10, 1), Day(10, 3), otaCommission: 50m);
        var byRule = await world.StayAsync(property, BookingSource.BookingCom, 300m, 0m, 0m, Day(10, 4), Day(10, 6));
        var byDefault = await world.StayAsync(property, BookingSource.Vrbo, 200m, 0m, 0m, Day(10, 7), Day(10, 9));
        var without = await world.StayAsync(property, BookingSource.Agoda, 150m, 0m, 0m, Day(10, 10), Day(10, 12));
        var direct = await world.StayAsync(property, BookingSource.Direct, 100m, 0m, 0m, Day(10, 13), Day(10, 15));

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var fees = (await world.LiveAsync()).Where(e => e.EntryType == OwnerLedgerEntryType.ChannelFee).ToDictionary(e => e.SourceId!.Value);

        Assert.Equal((-50m, ChannelFeeSources.Payload), (fees[withPayload].Amount, fees[withPayload].RuleVersion));
        Assert.Equal((-48m, ChannelFeeSources.Rule(rule.Id)), (fees[byRule].Amount, fees[byRule].RuleVersion));
        Assert.Equal((-16m, ChannelFeeSources.Default), (fees[byDefault].Amount, fees[byDefault].RuleVersion));
        Assert.False(fees.ContainsKey(without));
        Assert.False(fees.ContainsKey(direct));
        var missing = Assert.Single(result.Issues, i => i.Code == OwnerReadinessCodes.ChannelCommissionMissing);
        Assert.Equal((without, property, true), (missing.SourceId!.Value, missing.PropertyId!.Value, missing.Blocking));

        // A rule of the agency for Agoda releases the stay: its fee is posted, with the rule that made it.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var agoda = await world.Mandates.SaveChannelRulesAsync(
            OrgId,
            [
                new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 16m, ValidFrom = new DateOnly(2026, 1, 1) },
                new ChannelCommissionRuleCommand { Channel = BookingSource.Agoda, Percent = 10m, ValidFrom = new DateOnly(2026, 1, 1) },
            ],
            null);
        var again = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.DoesNotContain(again.Issues, i => i.Code == OwnerReadinessCodes.ChannelCommissionMissing);
        var agodaFee = Assert.Single(await world.LiveAsync(), e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.SourceId == without);
        Assert.Equal((-15m, ChannelFeeSources.Rule(agoda.Rules.Single(r => r.Channel == nameof(BookingSource.Agoda)).Id)), (agodaFee.Amount, agodaFee.RuleVersion));
    }

    /// <summary>
    /// Money/data: one path posts a stay. A payment recorded after the booking path posted the stay takes it over in the same month (the booking lines are
    /// reversed); recorded in a later month, the stay stays recognized where it was and the payment does not post it a second time.
    /// </summary>
    [Fact]
    public async Task Ledger_StayPostedOnceAcrossPaymentAndBookingPaths_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.MandateAsync(owner, 10m);

        var sameMonth = await world.StayAsync(property, BookingSource.Manual, 250m, 0m, 0m, Day(10, 15), Day(10, 20));
        var nextMonth = await world.StayAsync(property, BookingSource.Manual, 180m, 0m, 0m, Day(10, 25), Day(10, 28));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal(430m, RevenueOf(await world.LiveAsync()));

        // Paid in the same month: the payment posts it, the booking lines are reversed; the stay counts once.
        await world.PaymentAsync(sameMonth, 250m, new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc), method: PaymentMethod.BankTransfer);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var takeOver = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((2, 2), (takeOver.Posted, takeOver.Reversed));
        var live = await world.LiveAsync();
        Assert.Equal(430m, RevenueOf(live));
        Assert.Equal(OwnerLedgerSourceType.Payment, Assert.Single(live, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.Amount == 250m).SourceType);

        // Paid in November for a stay October already holds: November does not post it again.
        await world.PaymentAsync(nextMonth, 180m, new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc), method: PaymentMethod.BankTransfer);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var november = await world.Service.PostPeriodAsync(OrgId, 2026, 11, null);
        Assert.Equal(0, november.Posted);
        Assert.Equal(430m, RevenueOf(await world.LiveAsync()));

        // October posted again gives the stay up to its payment (its booking line is reversed), and November then posts it: once, all along.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal(250m, RevenueOf(await world.LiveAsync()));
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Service.PostPeriodAsync(OrgId, 2026, 11, null);
        var final = await world.LiveAsync();
        Assert.Equal(430m, RevenueOf(final));
        Assert.Equal(11, Assert.Single(final, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.Amount == 180m).PeriodMonth);
    }

    /// <summary>Money: the flat fees of the mandate are charged once per stay, split by the shares, and a second posting adds nothing.</summary>
    [Fact]
    public async Task Ledger_FlatFeesOncePerStay_Test()
    {
        var world = await World.CreateAsync();
        var first = await world.OwnerAsync();
        var second = await world.OwnerAsync();
        var property = await world.PropertyAsync((first, 60m), (second, 40m));
        var template = await world.Mandates.ApplyTemplateAsync(
            OrgWide,
            new OwnerMandateTemplateCommand
            {
                Template = new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 0m, CleaningFlatRate = 40m, LinenFlatRate = 15m },
            },
            null);
        Assert.Equal(2, template.Created);

        var paid = await world.StayAsync(property, BookingSource.Direct, 300m, 0m, 0m, Day(10, 10), Day(10, 12));
        await world.PaymentAsync(paid, 300m, new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));
        var portal = await world.StayAsync(property, BookingSource.Airbnb, 200m, 0m, 0m, Day(10, 20), Day(10, 22), otaCommission: 30m);

        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var flat = (await world.LiveAsync()).Where(e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee).ToList();

        // Two stays, two fees each, two owners: eight lines, each an owner charge of the recharge kind, split 60/40.
        Assert.Equal(8, flat.Count);
        Assert.All(flat, e => Assert.Equal(OwnerLedgerEntryType.OwnerCharge, e.EntryType));
        foreach (var stay in new[] { paid, portal })
        {
            var cleaning = flat.Where(e => e.SourceId == stay && e.SourceReference == OwnerLedgerFlatFees.Cleaning).ToList();
            var linen = flat.Where(e => e.SourceId == stay && e.SourceReference == OwnerLedgerFlatFees.Linen).ToList();
            Assert.Equal((-40m, -15m), (cleaning.Sum(e => e.Amount), linen.Sum(e => e.Amount)));
            Assert.Equal(-24m, cleaning.Single(e => e.PropertyOwnerId == first).Amount);
            Assert.Equal(new DateOnly(2026, 10, stay == paid ? 12 : 22), cleaning[0].RecognitionDate);
        }

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((0, 0), (again.Posted, again.Reversed));
        Assert.Equal(8, (await world.LiveAsync()).Count(e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee));
    }

    /// <summary>Money: the card fee of a payment through CasaZen is the owner's only when its mandate passes the payment fees through.</summary>
    [Fact]
    public async Task Ledger_PaymentFeesOnlyWhenTheMandatePassesThemThrough_Test()
    {
        var world = await World.CreateAsync();
        var passing = await world.OwnerAsync();
        var keeping = await world.OwnerAsync();
        var passed = await world.PropertyAsync((passing, 100m));
        var kept = await world.PropertyAsync((keeping, 100m));
        await world.MandateAsync(passing, 0m, passthrough: true);
        await world.MandateAsync(keeping, 0m, passthrough: false);

        var first = await world.StayAsync(passed, BookingSource.Direct, 450m, 0m, 0m, Day(10, 10), Day(10, 12));
        var payment = await world.PaymentAsync(first, 450m, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), providerFee: 6.55m);
        var second = await world.StayAsync(kept, BookingSource.Direct, 450m, 0m, 0m, Day(10, 10), Day(10, 12));
        await world.PaymentAsync(second, 450m, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), providerFee: 6.55m);

        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var expenses = (await world.LiveAsync()).Where(e => e.EntryType == OwnerLedgerEntryType.OwnerExpense).ToList();

        var expense = Assert.Single(expenses);
        Assert.Equal((passing, -6.55m, OwnerLedgerSourceType.Payment, payment), (expense.PropertyOwnerId, expense.Amount, expense.SourceType, expense.SourceId!.Value));
    }

    /// <summary>
    /// Money: a stay of an owner without a mandate in force posts its revenue and no commission, and flags the version (blocking) instead of a commission of
    /// zero; a mandate of the owner resolves it, in the ledger and in the readiness of the owner.
    /// </summary>
    [Fact]
    public async Task Ledger_NoMandate_FlagsInsteadOfZeroCommission_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        var stay = await world.StayAsync(property, BookingSource.Manual, 300m, 0m, 0m, Day(10, 8), Day(10, 10));

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var lines = await world.LiveAsync();
        Assert.Equal(300m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).Amount);
        Assert.DoesNotContain(lines, e => e.EntryType == OwnerLedgerEntryType.AgencyCommission);
        var issue = Assert.Single(result.Issues, i => i.Code == OwnerReadinessCodes.MandateMissingForStay);
        Assert.Equal((owner, stay, true), (issue.OwnerId!.Value, issue.SourceId!.Value, issue.Blocking));

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var flagged = await world.Service.GenerateVersionAsync(OrgId, owner, 2026, 10, null);
        Assert.Equal(OwnerStatementVersionStatus.Flagged, flagged.Status);
        Assert.Contains(OwnerReadinessCodes.MandateMissingForStay, flagged.IssuesJson!, StringComparison.Ordinal);

        // The readiness of the owner: with no mandate «mandate_missing», with the owner's own mandate (no ownership: all its properties) nothing.
        var ownership = new OwnerOwnershipFact(Guid.NewGuid(), property, new DateOnly(2026, 1, 1), null, 100m);
        Assert.Equal(OwnerReadinessReasons.MandateMissing, Assert.Single(OwnerReadinessRules.Mandate([ownership], [], new DateOnly(2026, 11, 14))).Code);
        Assert.Empty(OwnerReadinessRules.Mandate([ownership], [new OwnerMandateFact(null, new DateOnly(2026, 1, 1), null, HasDocument: true)], new DateOnly(2026, 11, 14)));

        await world.MandateAsync(owner, 15m);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var resolved = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.DoesNotContain(resolved.Issues, i => i.Code == OwnerReadinessCodes.MandateMissingForStay);
        Assert.Equal(-45m, Assert.Single(await world.LiveAsync(), e => e.EntryType == OwnerLedgerEntryType.AgencyCommission).Amount);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(OwnerStatementVersionStatus.Draft, (await world.Service.GenerateVersionAsync(OrgId, owner, 2026, 10, null)).Status);
    }

    /// <summary>
    /// Money: a price the host agreed by hand is an amount even when it is 0 (a stay for free): no revenue, nothing to tell, and the flat fees are charged; a
    /// booking that brought no price at all is still held back, never posted as a revenue of zero.
    /// </summary>
    [Fact]
    public async Task Ledger_AgreedPriceOfZero_IsNotAMissingAmount_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.Mandates.CreateVersionAsync(
            OrgWide, owner, new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 10m, CleaningFlatRate = 30m }, null);

        var free = await world.StayAsync(property, BookingSource.Manual, 0m, 0m, 0m, Day(10, 5), Day(10, 7), priceOverridden: true);
        var unknown = await world.StayAsync(property, BookingSource.Airbnb, 0m, 0m, 0m, Day(10, 10), Day(10, 12));

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var missing = Assert.Single(result.Issues, i => i.Code == OwnerReadinessCodes.SourceAmountMissing);
        Assert.Equal((unknown, true), (missing.SourceId!.Value, missing.Blocking));
        Assert.DoesNotContain(result.Issues, i => i.SourceId == free);

        var lines = await world.LiveAsync();
        Assert.DoesNotContain(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue);
        Assert.Equal(-30m, Assert.Single(lines, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee && e.SourceId == free).Amount);
    }

    /// <summary>
    /// Money: a stay of a portal is grossed from the total of its reservation less the tourist tax (the room, the cleaning and the extras the portal charged),
    /// never less than its room; the channel commission and the commission of the mandate are a percentage of that gross; a stay priced by CasaZen keeps its
    /// base price whatever its total.
    /// </summary>
    [Fact]
    public async Task Ledger_PortalStayGross_IsTheTotalLessTheTouristTax_Test()
    {
        Assert.Equal(560m, OwnerLedgerRules.GrossOfStay(true, 500m, 584m, 24m));
        Assert.Equal(500m, OwnerLedgerRules.GrossOfStay(true, 500m, 500m, 24m));
        Assert.Equal(500m, OwnerLedgerRules.GrossOfStay(true, 500m, 0m, 0m));
        Assert.Equal(500m, OwnerLedgerRules.GrossOfStay(false, 500m, 584m, 24m));

        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.MandateAsync(owner, 10m);

        // Room 500, tourist tax 24, and 60 of cleaning and extras the portal charged: the total is 584. Booking.com has no commission in the reservation: the default 15%.
        var portal = await world.StayAsync(property, BookingSource.BookingCom, 500m, 0m, 24m, Day(10, 10), Day(10, 13), total: 584m);
        var byHand = await world.StayAsync(property, BookingSource.Manual, 300m, 40m, 12m, Day(10, 20), Day(10, 22), total: 400m);

        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var lines = await world.LiveAsync();
        Assert.Equal(560m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceId == portal).Amount);
        Assert.Equal(-56m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.AgencyCommission && e.SourceId == portal).Amount);
        var fee = Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.SourceId == portal);
        Assert.Equal((-84m, ChannelFeeSources.Default), (fee.Amount, fee.RuleVersion));
        Assert.Equal(300m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue && e.SourceId == byHand).Amount);
    }

    /// <summary>
    /// Money/data: the flat fees of a stay are charged to the owners of the check-out day, a stay posted from its payments included; a property that has none that
    /// day (it was sold after the payment) tells it instead of losing the fees in silence.
    /// </summary>
    [Fact]
    public async Task Ledger_FlatFeesOfAPaidStayWithoutOwnersAtCheckOut_AreTold_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.Mandates.CreateVersionAsync(
            OrgWide, owner, new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 0m, CleaningFlatRate = 40m }, null);
        await world.EndOwnershipAsync(property, new DateTime(2026, 10, 10));
        var stay = await world.StayAsync(property, BookingSource.Direct, 300m, 0m, 0m, Day(10, 8), Day(10, 12));
        await world.PaymentAsync(stay, 300m, new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var told = Assert.Single(result.Issues, i => i.Code == OwnerReadinessCodes.OwnershipMissing && i.SourceId == stay);
        Assert.True(told.Blocking);
        Assert.DoesNotContain(await world.LiveAsync(), e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee);
    }

    /// <summary>
    /// Money: only the active stays are posted (confirmed, checked in or checked out, never a no-show): a pending, a cancelled and a no-show booking post no
    /// revenue, no commission and no flat fee; a stay cancelled after it was posted loses its lines at the next posting (a reversal).
    /// </summary>
    [Fact]
    public async Task Ledger_OnlyActiveStaysArePosted_CancelledAfterPostingIsReversed_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.Mandates.CreateVersionAsync(
            OrgWide, owner, new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 10m, CleaningFlatRate = 25m }, null);

        var confirmed = await world.StayAsync(property, BookingSource.Manual, 100m, 0m, 0m, Day(10, 2), Day(10, 4));
        var checkedOut = await world.StayAsync(property, BookingSource.Manual, 200m, 0m, 0m, Day(10, 5), Day(10, 7), status: BookingStatus.CheckedOut);
        await world.StayAsync(property, BookingSource.Manual, 400m, 0m, 0m, Day(10, 8), Day(10, 10), status: BookingStatus.Pending);
        await world.StayAsync(property, BookingSource.Manual, 800m, 0m, 0m, Day(10, 11), Day(10, 13), status: BookingStatus.Cancelled);
        await world.StayAsync(property, BookingSource.Manual, 1600m, 0m, 0m, Day(10, 14), Day(10, 16), noShow: true);

        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var lines = await world.LiveAsync();
        Assert.Equal(300m, RevenueOf(lines));
        Assert.Equal(-30m, lines.Where(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission).Sum(e => e.Amount));
        var cleaned = lines.Where(e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee).Select(e => e.SourceId!.Value).Order();
        Assert.Equal(new[] { confirmed, checkedOut }.Order(), cleaned);

        // The stay cancelled after the posting: its revenue, its commission and its flat fee are reversed, nothing else moves.
        await world.SetStatusAsync(confirmed, BookingStatus.Cancelled);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var after = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((0, 3), (after.Posted, after.Reversed));
        var live = await world.LiveAsync();
        Assert.Equal(200m, RevenueOf(live));
        Assert.DoesNotContain(live, e => e.SourceId == confirmed);
    }

    /// <summary>
    /// Money: the mandate in force on the day of a stay is the one that counts, for the commission and for the flat fees: of two stays of one month, the one that
    /// checks out the day before a new version and the one that checks out on its first day are charged each by its own version.
    /// </summary>
    [Fact]
    public async Task Ledger_MandateInForceOnTheDayOfTheStay_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.Mandates.CreateVersionAsync(OrgWide, owner, new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 10m }, null);
        await world.Mandates.CreateVersionAsync(
            OrgWide, owner, new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 10, 15), CommissionPercent = 20m, CleaningFlatRate = 30m }, null);

        var before = await world.StayAsync(property, BookingSource.Manual, 500m, 0m, 0m, Day(10, 12), Day(10, 14));
        var onTheDay = await world.StayAsync(property, BookingSource.Manual, 500m, 0m, 0m, Day(10, 13), Day(10, 15));

        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var lines = await world.LiveAsync();
        Assert.Equal(-50m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.AgencyCommission && e.SourceId == before).Amount);
        Assert.Equal(-100m, Assert.Single(lines, e => e.EntryType == OwnerLedgerEntryType.AgencyCommission && e.SourceId == onTheDay).Amount);
        var flat = Assert.Single(lines, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee);
        Assert.Equal((onTheDay, -30m), (flat.SourceId!.Value, flat.Amount));
    }

    /// <summary>
    /// Money/data: a channel rule that posted lines name is the evidence of the commission they took: it is never removed nor given another percent or base, only
    /// closed (its last day moves) with the next rule starting after it; a rule nothing names changes and goes freely.
    /// </summary>
    [Fact]
    public async Task ChannelRules_NamedByPostedLines_AreClosedNeverRemovedNorChanged_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync((owner, 100m));
        await world.MandateAsync(owner, 0m);
        var booking = new ChannelCommissionRuleCommand { Channel = BookingSource.BookingCom, Percent = 16m, ValidFrom = new DateOnly(2026, 1, 1) };
        var agoda = new ChannelCommissionRuleCommand { Channel = BookingSource.Agoda, Percent = 10m, ValidFrom = new DateOnly(2026, 1, 1) };
        var saved = await world.Mandates.SaveChannelRulesAsync(OrgId, [booking, agoda], null);
        var bookingRule = saved.Rules.Single(r => r.Channel == nameof(BookingSource.BookingCom));
        var agodaRule = saved.Rules.Single(r => r.Channel == nameof(BookingSource.Agoda));

        // October: the fee of a Booking.com stay names the Booking.com rule; nothing names the Agoda one.
        await world.StayAsync(property, BookingSource.BookingCom, 300m, 0m, 0m, Day(10, 4), Day(10, 6));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal(ChannelFeeSources.Rule(bookingRule.Id), Assert.Single(await world.LiveAsync(), e => e.EntryType == OwnerLedgerEntryType.ChannelFee).RuleVersion);

        // Removed, or given another percent or base: refused with the id of the rule, nothing written.
        var refusals = new[]
        {
            new[] { agoda },
            new[] { booking with { Percent = 17m }, agoda },
            new[] { booking with { Base = CommissionBase.GrossExclTouristTaxAndCleaning }, agoda },
        };
        foreach (var list in refusals)
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(() => world.Mandates.SaveChannelRulesAsync(OrgId, list, null));
            Assert.Equal(OwnerMandateErrors.ChannelRuleInUse, refused.Code);
            Assert.Equal(bookingRule.Id, refused.Extensions["ruleId"]);
        }

        Assert.Equal(2, await world.Db.OrgChannelCommissionRules.CountAsync());
        var stored = await world.Db.OrgChannelCommissionRules.AsNoTracking().SingleAsync(r => r.Id == bookingRule.Id);
        Assert.Equal((16m, CommissionBase.GrossExclTouristTax), (stored.Percent, stored.Base));

        // Closed, and the next rule from the following day: the Booking.com rule keeps its id; the Agoda one, which nothing names, is changed in the same row.
        var closing = new[]
        {
            booking with { ValidTo = new DateOnly(2026, 12, 31) },
            booking with { Percent = 17m, ValidFrom = new DateOnly(2027, 1, 1) },
            agoda with { Percent = 12m },
        };
        var closed = await world.Mandates.SaveChannelRulesAsync(OrgId, closing, null);
        Assert.Equal(3, closed.Rules.Count);
        var kept = closed.Rules.Single(r => r.Channel == nameof(BookingSource.BookingCom) && r.ValidFrom == new DateOnly(2026, 1, 1));
        Assert.Equal((bookingRule.Id, new DateOnly(2026, 12, 31), 16m), (kept.Id, kept.ValidTo!.Value, kept.Percent));
        var changed = closed.Rules.Single(r => r.Channel == nameof(BookingSource.Agoda));
        Assert.Equal((agodaRule.Id, 12m), (changed.Id, changed.Percent));

        // The Agoda rule, named by nothing, can go.
        var withoutAgoda = new[] { closing[0], closing[1] };
        var gone = await world.Mandates.SaveChannelRulesAsync(OrgId, withoutAgoda, null);
        Assert.DoesNotContain(gone.Rules, r => r.Channel == nameof(BookingSource.Agoda));
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static decimal RevenueOf(IEnumerable<OwnerLedgerEntry> lines) => lines.Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).Sum(e => e.Amount);

    /// <summary>A Swiss agency with its owners, properties, stays and payments, the owner accounting and the mandates over one in-memory database.</summary>
    private sealed class World
    {
        private World(AppDbContext db)
        {
            Db = db;
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
            var defaults = new OwnerAccountingOptions();
            foreach (var (channel, percent) in new[] { ("BookingCom", 15m), ("Expedia", 18m), ("Airbnb", 15.5m), ("Vrbo", 8m) })
                defaults.ChannelCommissionDefaults[channel] = percent;
            var options = Microsoft.Extensions.Options.Options.Create(defaults);
            Fiscal = new OwnerFiscalProfileService(db, Clock, NullLogger<OwnerFiscalProfileService>.Instance);
            Service = new OwnerAccountingService(db, Fiscal, Clock, NullLogger<OwnerAccountingService>.Instance, options);
            Mandates = new OwnerMandateService(db, Clock, options, NullLogger<OwnerMandateService>.Instance);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public OwnerAccountingService Service { get; }

        public OwnerMandateService Mandates { get; }

        public static Task<World> CreateAsync() => Task.FromResult(new World(new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerPosting_{Guid.NewGuid()}").Options,
            new PostingTenant(),
            new EphemeralDataProtectionProvider())));

        /// <summary>A Swiss owner paid in francs, with a complete profile from January and the approved Swiss rule set (created once).</summary>
        public async Task<Guid> OwnerAsync()
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();

            var policy = (await Fiscal.ListPoliciesAsync(OrgId)).FirstOrDefault();
            if (policy is null)
            {
                policy = await Fiscal.CreatePolicyAsync(OrgId, new OwnerAccountingPolicyCommand
                {
                    Jurisdiction = "CH",
                    RuleCode = "TEST-CH",
                    Version = "1",
                    ValidFrom = new DateOnly(2026, 1, 1),
                    SourceUrl = "https://example.com/test-rule",
                    SourceSha256 = new string('d', 64),
                    Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 10 },
                }, null);
                await Fiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", null);
            }

            await Fiscal.SaveProfileAsync(OrgId, owner.Id, new OwnerFiscalProfileCommand
            {
                ValidFrom = new DateOnly(2026, 1, 1),
                EntityKind = OwnerEntityKind.NaturalPerson,
                LegalName = "TEST Proprietario",
                TaxResidenceCountry = "CH",
                LegalAddress = "Via del Test 1, Lugano",
                ForeignTaxId = "756123456789",
                SettlementCurrency = "CHF",
                BankAccountReference = "CH9300762011623852957",
                RevenueRecognitionRule = "cash",
                TaxJurisdiction = "CH",
                AccountingPolicyId = policy.Id,
            }, null);
            return owner.Id;
        }

        /// <summary>A Ticino property in francs owned from January by the given owners.</summary>
        public async Task<Guid> PropertyAsync(params (Guid Owner, decimal Share)[] owners)
        {
            var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = "Lugano", Currency = "CHF" };
            Db.Properties.Add(property);
            foreach (var (owner, share) in owners)
            {
                Db.PropertyOwnerships.Add(new PropertyOwnership
                {
                    OrgId = OrgId,
                    PropertyId = property.Id,
                    PropertyOwnerId = owner,
                    SharePercent = share,
                    OwnershipStart = new DateTime(2026, 1, 1),
                });
            }

            await Db.SaveChangesAsync();
            return property.Id;
        }

        /// <summary>A mandate of the owner from January, through the API's service.</summary>
        public Task<OwnerMandate> MandateAsync(Guid owner, decimal commission, bool passthrough = false) =>
            Mandates.CreateVersionAsync(
                OrgWide, owner, new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = commission, PassthroughPaymentFees = passthrough }, null);

        /// <summary>
        /// A stay, confirmed unless said otherwise: its gross without the tourist tax (the cleaning in it), the tourist tax and the total the guest owes (the gross
        /// and the tax unless <paramref name="total"/> says another: what a portal charges for the cleaning and the extras is in it).
        /// </summary>
        public async Task<Guid> StayAsync(
            Guid property,
            BookingSource source,
            decimal basePrice,
            decimal cleaning,
            decimal touristTax,
            DateTime checkIn,
            DateTime checkOut,
            decimal? otaCommission = null,
            BookingStatus status = BookingStatus.Confirmed,
            bool noShow = false,
            decimal? total = null,
            bool priceOverridden = false)
        {
            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property,
                GuestId = guest.Id,
                CheckInDate = checkIn,
                CheckOutDate = checkOut,
                NumberOfGuests = 2,
                Status = status,
                NoShowAt = noShow ? checkOut : null,
                Source = source,
                BasePrice = basePrice,
                CleaningFee = cleaning,
                TouristTax = touristTax,
                TouristTaxAmount = touristTax,
                TotalPrice = total ?? basePrice + touristTax,
                PriceOverridden = priceOverridden,
                OtaCommission = otaCommission,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            await Db.SaveChangesAsync();
            return booking.Id;
        }

        /// <summary>A completed payment of the booking: by card through CasaZen (with the fee the provider kept), or recorded by the agency.</summary>
        public async Task<Guid> PaymentAsync(
            Guid booking,
            decimal amount,
            DateTime paidAt,
            PaymentMethod method = PaymentMethod.CreditCard,
            decimal? providerFee = null)
        {
            var payment = new Payment
            {
                OrgId = OrgId,
                BookingId = booking,
                Amount = amount,
                Status = PaymentStatus.Completed,
                Method = method,
                TransactionId = "TEST-" + Guid.NewGuid().ToString("N"),
                Description = "TEST",
                ProcessedAt = paidAt,
                ProviderFeeAmount = providerFee,
                ProviderBalanceCurrency = providerFee is null ? null : "CHF",
            };
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            return payment.Id;
        }

        /// <summary>The ownership of the property ends on the day: the owners hold it until the day before.</summary>
        public async Task EndOwnershipAsync(Guid property, DateTime end)
        {
            var ownership = await Db.PropertyOwnerships.SingleAsync(o => o.PropertyId == property);
            ownership.OwnershipEnd = end;
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>The booking moves to another status (a cancellation after the month was posted).</summary>
        public async Task SetStatusAsync(Guid booking, BookingStatus status)
        {
            var row = await Db.Bookings.SingleAsync(b => b.Id == booking);
            row.Status = status;
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>A refund as PaymentRefundService leaves it: the refunded amount and the state that follows.</summary>
        public async Task RefundAsync(Guid paymentId, decimal refunded)
        {
            var payment = await Db.Payments.SingleAsync(p => p.Id == paymentId);
            payment.RefundedAmount = refunded;
            payment.Status = refunded >= payment.Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>The lines that count: neither reversals nor reversed.</summary>
        public async Task<List<OwnerLedgerEntry>> LiveAsync()
        {
            var lines = await Db.OwnerLedgerEntries.AsNoTracking().ToListAsync();
            var reversed = lines.Where(e => e.ReversesEntryId is not null).Select(e => e.ReversesEntryId!.Value).ToHashSet();
            return lines.Where(e => e.ReversesEntryId is null && !reversed.Contains(e.Id)).ToList();
        }
    }
}

file sealed class PostingTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
