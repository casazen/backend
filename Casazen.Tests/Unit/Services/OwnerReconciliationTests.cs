using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.OwnerMonthEndWorld;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E13/F01/US03/T02 (NF-110, NF-115, acceptance step C1): before a month is closed the agency sees, per channel, what the owner ledgers posted for the stays, the commissions of
/// the portals and where they come from, the payout the portal is expected to have made and the one recorded, and the published statements against the ledger, every
/// difference named by its stay or its entry. Read only, the org's own data only, and the amounts of a currency are never added to those of another.
/// </summary>
public class OwnerReconciliationTests
{
    /// <summary>
    /// Money: a month with stays of three portals, the agency's own site and a stay the host collected: each row has the stays and the gross posted, the commission split by its
    /// source, the payout expected and the one recorded, and the difference; every difference is listed by its stay with its reason, before the statements are published
    /// (the whole ledger is a difference) and after (only what was posted since is), and the listed differences always add up to the difference of the totals.
    /// </summary>
    [Fact]
    public async Task Reconciliation_DifferencesListedPerStay_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync("CHF", (owner, 100m));
        await world.MandateAsync(owner, 20m);
        world.Db.OrgChannelCommissionRules.Add(new OrgChannelCommissionRule
        {
            OrgId = world.OrgId,
            Channel = nameof(BookingSource.BookingCom),
            Percent = 12m,
            ValidFrom = new DateOnly(2026, 1, 1),
            CreatedAt = DateTime.UtcNow,
        });
        await world.Db.SaveChangesAsync();

        // The portal's own commission and net, and what the channel manager says it paid: agrees.
        var agreed = await world.StayAsync(property, BookingSource.BookingCom, 100m, Day(10, 2), Day(10, 4), otaCommission: 15m, otaNet: 85m);
        // No commission in the reservation: the configured default of the channel (15.5%), and no net reported: nothing recorded.
        var unrecorded = await world.StayAsync(property, BookingSource.Airbnb, 200m, Day(10, 5), Day(10, 8));
        // The portal paid 240 where 246 is expected.
        var short1 = await world.StayAsync(property, BookingSource.Expedia, 300m, Day(10, 9), Day(10, 12), otaCommission: 54m, otaNet: 240m);
        // The host collected it: no payout from the portal.
        var hostCollected = await world.StayAsync(property, BookingSource.BookingCom, 400m, Day(10, 13), Day(10, 16), otaCommission: 60m, otaNet: 340m, companyCollect: false);
        // The agency's own site: a channel without a commission or a payout.
        var direct = await world.StayAsync(property, BookingSource.Direct, 150m, Day(10, 17), Day(10, 20));
        // No commission in the reservation: the rule of the agency (12%) wins over the default (15%).
        var byRule = await world.StayAsync(property, BookingSource.BookingCom, 250m, Day(10, 21), Day(10, 24), otaNet: 220m);
        var posted = await world.Service.PostPeriodAsync(world.OrgId, 2026, 10, UserId);
        Assert.DoesNotContain(posted.Issues, i => i.Blocking);

        var before = await world.Reconciliation.GetAsync(world.OrgId, 2026, 10);

        var booking = Assert.Single(before.Channels, r => r.Channel == BookingSource.BookingCom);
        Assert.Equal(("CHF", true, 3, 750m), (booking.Currency, booking.IsPortal, booking.Stays, booking.LedgerGross));
        Assert.Equal((75m, 30m, 0m, 105m, 30m), (booking.Commissions.FromPortal, booking.Commissions.FromRule, booking.Commissions.FromDefault, booking.Commissions.Total, booking.Commissions.OnRule));
        Assert.Equal(305m, booking.ExpectedNetPayout);
        Assert.Equal(305m, booking.RecordedPayouts);
        Assert.Equal(0m, booking.Difference);
        Assert.Equal(0, booking.StaysWithoutRecordedPayout);
        Assert.Equal(1, booking.HostCollectedStays);
        var airbnb = Assert.Single(before.Channels, r => r.Channel == BookingSource.Airbnb);
        Assert.Equal((1, 200m, 31m, 0m, 0m), (airbnb.Stays, airbnb.LedgerGross, airbnb.Commissions.FromDefault, airbnb.Commissions.FromPortal, airbnb.Commissions.FromRule));
        Assert.Equal(169m, airbnb.ExpectedNetPayout);

        // No payment in CasaZen and no net reported: nothing is recorded because nothing is available, and not available is never a zero.
        Assert.Null(airbnb.RecordedPayouts);
        Assert.Null(airbnb.Difference);
        Assert.Equal(1, airbnb.StaysWithoutRecordedPayout);
        var expedia = Assert.Single(before.Channels, r => r.Channel == BookingSource.Expedia);
        Assert.Equal((300m, 54m), (expedia.LedgerGross, expedia.Commissions.FromPortal));
        Assert.Equal(246m, expedia.ExpectedNetPayout);
        Assert.Equal(240m, expedia.RecordedPayouts);
        Assert.Equal(-6m, expedia.Difference);
        Assert.Equal(0, expedia.StaysWithoutRecordedPayout);
        var site = Assert.Single(before.Channels, r => r.Channel == BookingSource.Direct);
        Assert.Equal((false, 1, 150m, 0m), (site.IsPortal, site.Stays, site.LedgerGross, site.Commissions.Total));
        Assert.Null(site.ExpectedNetPayout);
        Assert.Null(site.RecordedPayouts);
        Assert.Null(site.Difference);
        Assert.Equal(4, before.Channels.Count);

        // The payouts that are not the ones expected, by their stay with the reason.
        var payouts = before.Differences.Where(d => d.Reason is OwnerReconciliationReasons.PayoutSourceUnavailable or OwnerReconciliationReasons.PayoutDiffers).ToList();
        Assert.Equal(2, payouts.Count);
        var missing = Assert.Single(payouts, d => d.Id == unrecorded);
        Assert.Equal((ReconciliationDifferenceKind.Stay, OwnerReconciliationReasons.PayoutSourceUnavailable, "CHF"), (missing.Kind, missing.Reason, missing.Currency));
        Assert.Equal(169m, missing.Expected);
        Assert.Equal(BookingSource.Airbnb, missing.Channel);
        Assert.Null(missing.Recorded);
        Assert.Null(missing.Difference);
        var differs = Assert.Single(payouts, d => d.Id == short1);
        Assert.Equal(OwnerReconciliationReasons.PayoutDiffers, differs.Reason);
        Assert.Equal(-6m, differs.Difference);
        Assert.Equal(246m, differs.Expected);
        Assert.Equal(240m, differs.Recorded);
        Assert.DoesNotContain(before.Differences, d => d.Id == agreed && d.Reason is OwnerReconciliationReasons.PayoutSourceUnavailable or OwnerReconciliationReasons.PayoutDiffers);
        Assert.DoesNotContain(before.Differences, d => d.Id == hostCollected && d.Reason is OwnerReconciliationReasons.PayoutSourceUnavailable or OwnerReconciliationReasons.PayoutDiffers);
        Assert.DoesNotContain(before.Differences, d => d.Reason == OwnerReconciliationReasons.StayNotPosted);

        // Nothing is published: the whole ledger (930) is the difference, listed stay by stay (the lines of a stay together).
        var openStatements = Assert.Single(before.Statements);
        Assert.Equal(("CHF", 1, 0, 930m, 0m, -930m), (openStatements.Currency, openStatements.LedgerOwners, openStatements.PublishedStatements, openStatements.LedgerTotal, openStatements.PublishedTotal, openStatements.Difference));
        var unpublished = before.Differences.Where(d => d.Reason == OwnerReconciliationReasons.StatementNotPublished).ToList();
        Assert.Equal(6, unpublished.Count);
        Assert.All(unpublished, d =>
        {
            Assert.Equal(ReconciliationDifferenceKind.Stay, d.Kind);
            Assert.Equal(owner, d.OwnerId);
            Assert.Equal(0m, d.Recorded);
        });
        Assert.Equal(new decimal[] { 65m, 129m, 186m, 260m, 120m, 170m }, new[] { agreed, unrecorded, short1, hostCollected, direct, byRule }.Select(stay => unpublished.Single(d => d.Id == stay).Expected!.Value));
        Assert.Equal(-930m, unpublished.Sum(d => d.Difference));

        // The statement is published; then a stay is posted after its cut-off, and another is not posted at all.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var version = await world.Service.GenerateVersionAsync(world.OrgId, owner, 2026, 10, UserId);
        Assert.Equal(OwnerStatementVersionStatus.Draft, version.Status);
        await world.Service.ApproveVersionAsync(world.OrgId, version.Id, UserId);
        await world.Service.PublishVersionAsync(world.OrgId, version.Id, UserId);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var late = await world.StayAsync(property, BookingSource.Direct, 50m, Day(10, 26), Day(10, 28));
        await world.Service.PostPeriodAsync(world.OrgId, 2026, 10, UserId);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var forgotten = await world.StayAsync(property, BookingSource.Direct, 60m, Day(10, 29), Day(10, 30));

        var after = await world.Reconciliation.GetAsync(world.OrgId, 2026, 10);

        var statements = Assert.Single(after.Statements);
        Assert.Equal(("CHF", 1, 1, 970m, 930m, -40m), (statements.Currency, statements.LedgerOwners, statements.PublishedStatements, statements.LedgerTotal, statements.PublishedTotal, statements.Difference));
        var statementDifferences = after.Differences.Where(d => d.Reason is OwnerReconciliationReasons.StatementNotPublished or OwnerReconciliationReasons.PostedAfterPublication or OwnerReconciliationReasons.StatementTotalDiffers).ToList();
        var afterPublication = Assert.Single(statementDifferences);
        Assert.Equal((ReconciliationDifferenceKind.Stay, late, OwnerReconciliationReasons.PostedAfterPublication, (decimal?)-40m), (afterPublication.Kind, afterPublication.Id, afterPublication.Reason, afterPublication.Difference));
        Assert.Equal(40m, afterPublication.Expected);
        Assert.Equal(0m, afterPublication.Recorded);
        Assert.Equal(BookingSource.Direct, afterPublication.Channel);
        Assert.Equal(owner, afterPublication.OwnerId);
        Assert.Equal(statements.Difference, statementDifferences.Sum(d => d.Difference));

        // A stay of the month with a gross to post that the ledger does not hold is told by its booking.
        var notPosted = Assert.Single(after.Differences, d => d.Reason == OwnerReconciliationReasons.StayNotPosted);
        Assert.Equal((forgotten, ReconciliationDifferenceKind.Stay, "CHF", (decimal?)-60m), (notPosted.Id, notPosted.Kind, notPosted.Currency, notPosted.Difference));
        Assert.Equal(60m, notPosted.Expected);
        Assert.Equal(0m, notPosted.Recorded);
        Assert.Equal(BookingSource.Direct, notPosted.Channel);
    }

    /// <summary>
    /// Money: a payout CasaZen has no figure for is not available, never a zero. A row with a figure for some of its stays compares only those (the listed differences add up to its
    /// difference) and counts the others; the stay without a figure is told with its own reason and a <c>null</c> recorded figure and difference.
    /// </summary>
    [Fact]
    public async Task Reconciliation_AMixedRowComparesOnlyTheStaysThatHaveAFigure_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync("CHF", (owner, 100m));
        await world.MandateAsync(owner, 20m);
        var known = await world.StayAsync(property, BookingSource.Expedia, 300m, Day(10, 2), Day(10, 5), otaCommission: 54m, otaNet: 240m);
        var unknown = await world.StayAsync(property, BookingSource.Expedia, 100m, Day(10, 6), Day(10, 8), otaCommission: 18m);
        var posted = await world.Service.PostPeriodAsync(world.OrgId, 2026, 10, UserId);
        Assert.DoesNotContain(posted.Issues, i => i.Blocking);

        var result = await world.Reconciliation.GetAsync(world.OrgId, 2026, 10);

        var row = Assert.Single(result.Channels);
        Assert.Equal(246m + 82m, row.ExpectedNetPayout);
        Assert.Equal(240m, row.RecordedPayouts);
        Assert.Equal(-6m, row.Difference);
        Assert.Equal(1, row.StaysWithoutRecordedPayout);
        var payouts = result.Differences.Where(d => d.Reason is OwnerReconciliationReasons.PayoutSourceUnavailable or OwnerReconciliationReasons.PayoutDiffers).ToList();
        Assert.Equal(2, payouts.Count);
        var differs = Assert.Single(payouts, d => d.Id == known);
        Assert.Equal(OwnerReconciliationReasons.PayoutDiffers, differs.Reason);
        Assert.Equal(246m, differs.Expected);
        Assert.Equal(240m, differs.Recorded);
        Assert.Equal(-6m, differs.Difference);
        var unavailable = Assert.Single(payouts, d => d.Id == unknown);
        Assert.Equal(OwnerReconciliationReasons.PayoutSourceUnavailable, unavailable.Reason);
        Assert.Equal(82m, unavailable.Expected);
        Assert.Null(unavailable.Recorded);
        Assert.Null(unavailable.Difference);
        Assert.Equal(row.Difference, payouts.Sum(d => d.Difference));
    }

    /// <summary>
    /// Money: what is recorded for a stay from the payments collected in CasaZen is what the owner ledger posts for them: each payment net of its refunds and without its part of the
    /// tourist tax (so it is compared with an expected payout that has none in it), a payment refunded in full counts 0, and the payments win over a net the channel manager reported.
    /// </summary>
    [Fact]
    public async Task Reconciliation_PaymentsCollectedInCasaZenAreRecordedWithoutTheTouristTaxAndNetOfRefunds_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync("CHF", (owner, 100m));
        await world.MandateAsync(owner, 20m);

        // Three portal stays of 100 plus 10 of tourist tax, with 15 of commission in the reservation and a net of 85 reported.
        // Paid in full: 110 collected, 10 of them tourist tax: 100 are recorded, and the net reported does not count (the payments win).
        var full = await world.StayAsync(property, BookingSource.BookingCom, 100m, Day(10, 2), Day(10, 4), touristTax: 10m, otaCommission: 15m, otaNet: 85m);
        await world.PaymentAsync(full, 110m, Day(10, 1));

        // Half of it refunded: 55 are left, 5 of them tourist tax: 50 are recorded.
        var halfRefunded = await world.StayAsync(property, BookingSource.Expedia, 100m, Day(10, 5), Day(10, 7), touristTax: 10m, otaCommission: 15m, otaNet: 85m);
        await RefundAsync(world, await world.PaymentAsync(halfRefunded, 110m, Day(10, 1)), 55m);

        // A payment refunded in full counts 0: of the two payments of this stay only the one of 55 keeps its money (5 of tourist tax): 50 are recorded.
        var refundedInFull = await world.StayAsync(property, BookingSource.Airbnb, 100m, Day(10, 8), Day(10, 10), touristTax: 10m, otaCommission: 15m, otaNet: 85m);
        await RefundAsync(world, await world.PaymentAsync(refundedInFull, 110m, Day(10, 1)), 110m);
        await world.PaymentAsync(refundedInFull, 55m, Day(10, 2));
        var posted = await world.Service.PostPeriodAsync(world.OrgId, 2026, 10, UserId);
        Assert.DoesNotContain(posted.Issues, i => i.Blocking);

        var result = await world.Reconciliation.GetAsync(world.OrgId, 2026, 10);

        // Expected is what the ledger posted (the revenue of the payments, without the tourist tax) less the commission of the reservation; recorded is the payments as posted.
        var checks = new[] { (Stay: full, Expected: 85m, Recorded: 100m), (halfRefunded, 35m, 50m), (refundedInFull, 35m, 50m) };
        foreach (var check in checks)
        {
            var difference = Assert.Single(result.Differences, d => d.Id == check.Stay && d.Reason == OwnerReconciliationReasons.PayoutDiffers);
            Assert.Equal(check.Expected, difference.Expected);
            Assert.Equal(check.Recorded, difference.Recorded);
            Assert.Equal(check.Recorded - check.Expected, difference.Difference);
        }

        Assert.Equal(100m, Assert.Single(result.Channels, r => r.Channel == BookingSource.BookingCom).RecordedPayouts);
        Assert.Equal(50m, Assert.Single(result.Channels, r => r.Channel == BookingSource.Expedia).RecordedPayouts);
        Assert.Equal(50m, Assert.Single(result.Channels, r => r.Channel == BookingSource.Airbnb).RecordedPayouts);
    }

    /// <summary>
    /// Money: amounts of different currencies are never summed: a channel has one row for each currency, the statements one row for each statement currency, and every
    /// difference carries its own currency.
    /// </summary>
    [Fact]
    public async Task Reconciliation_NeverSumsAcrossCurrencies_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var swiss = await world.OwnerAsync("CH", "CHF");
        var italian = await world.OwnerAsync("IT", "EUR");
        var swissHouse = await world.PropertyAsync("CHF", (swiss, 100m));
        var italianHouse = await world.PropertyAsync("EUR", (italian, 100m));
        await world.MandateAsync(swiss, 20m);
        await world.MandateAsync(italian, 20m);
        await world.StayAsync(swissHouse, BookingSource.BookingCom, 100m, Day(10, 2), Day(10, 4), otaCommission: 15m, otaNet: 85m);
        var euroStay = await world.StayAsync(italianHouse, BookingSource.BookingCom, 100m, Day(10, 5), Day(10, 7), otaCommission: 15m, otaNet: 80m);
        var posted = await world.Service.PostPeriodAsync(world.OrgId, 2026, 10, UserId);
        Assert.DoesNotContain(posted.Issues, i => i.Blocking);

        // Only the Swiss owner's statement is published.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var version = await world.Service.GenerateVersionAsync(world.OrgId, swiss, 2026, 10, UserId);
        await world.Service.ApproveVersionAsync(world.OrgId, version.Id, UserId);
        await world.Service.PublishVersionAsync(world.OrgId, version.Id, UserId);

        var result = await world.Reconciliation.GetAsync(world.OrgId, 2026, 10);

        // Two rows for one channel, one for each currency: nothing of 200 anywhere.
        Assert.Equal(["CHF", "EUR"], result.Channels.Select(r => r.Currency));
        Assert.All(result.Channels, r =>
        {
            Assert.Equal((BookingSource.BookingCom, 1, 100m), (r.Channel, r.Stays, r.LedgerGross));
            Assert.Equal(85m, r.ExpectedNetPayout);
        });
        Assert.Equal(0m, result.Channels[0].Difference);
        Assert.Equal(-5m, result.Channels[1].Difference);
        Assert.Equal(85m, result.Channels[0].RecordedPayouts);
        Assert.Equal(80m, result.Channels[1].RecordedPayouts);

        // The statements: francs and euros apart, each against its own ledger (65 = 100 less 15 of portal commission and 20 of commission).
        Assert.Equal(["CHF", "EUR"], result.Statements.Select(r => r.Currency));
        Assert.Equal((65m, 65m, 0m, 1), (result.Statements[0].LedgerTotal, result.Statements[0].PublishedTotal, result.Statements[0].Difference, result.Statements[0].PublishedStatements));
        Assert.Equal((65m, 0m, -65m, 0), (result.Statements[1].LedgerTotal, result.Statements[1].PublishedTotal, result.Statements[1].Difference, result.Statements[1].PublishedStatements));

        // Every difference is in its own currency and names its own owner or stay.
        var euros = result.Differences.Where(d => d.Currency == "EUR").ToList();
        Assert.Equal([OwnerReconciliationReasons.PayoutDiffers, OwnerReconciliationReasons.StatementNotPublished], euros.Select(d => d.Reason).Order(StringComparer.Ordinal));
        Assert.All(euros, d => Assert.Equal(euroStay, d.Id));
        Assert.DoesNotContain(result.Differences, d => d.Currency == "CHF");
        Assert.Equal(-5m + -65m, euros.Sum(d => d.Difference));
    }

    /// <summary>Data (tenant isolation): only the org's own ledger, bookings and statements are read, and nothing is written.</summary>
    [Fact]
    public async Task Reconciliation_ReadsOnlyTheOrgAndWritesNothing_Test()
    {
        var mine = await OwnerMonthEndWorld.CreateAsync();
        var owner = await mine.OwnerAsync();
        var house = await mine.PropertyAsync("CHF", (owner, 100m));
        await mine.MandateAsync(owner, 20m);
        await mine.StayAsync(house, BookingSource.BookingCom, 100m, Day(10, 2), Day(10, 4), otaCommission: 15m, otaNet: 85m);
        await mine.Service.PostPeriodAsync(mine.OrgId, 2026, 10, UserId);

        // Another agency in the same database, with a posted month of its own, a stay not posted and a published statement.
        var other = await OwnerMonthEndWorld.CreateAsync(sameDatabaseAs: mine);
        var stranger = await other.OwnerAsync();
        var elsewhere = await other.PropertyAsync("CHF", (stranger, 100m));
        await other.MandateAsync(stranger, 20m);
        await other.StayAsync(elsewhere, BookingSource.Airbnb, 900m, Day(10, 2), Day(10, 4));
        await other.Service.PostPeriodAsync(other.OrgId, 2026, 10, UserId);
        other.Clock.Advance(TimeSpan.FromMinutes(1));
        var theirs = await other.Service.GenerateVersionAsync(other.OrgId, stranger, 2026, 10, UserId);
        await other.Service.ApproveVersionAsync(other.OrgId, theirs.Id, UserId);
        await other.Service.PublishVersionAsync(other.OrgId, theirs.Id, UserId);
        await other.StayAsync(elsewhere, BookingSource.Direct, 77m, Day(10, 20), Day(10, 22));
        mine.Db.ChangeTracker.Clear();
        var counts = (mine.Db.OwnerLedgerEntries.Count(), mine.Db.OwnerStatementVersions.Count(), mine.Db.Bookings.Count(), mine.Db.OwnerClosePeriods.Count());

        var result = await mine.Reconciliation.GetAsync(mine.OrgId, 2026, 10);

        var row = Assert.Single(result.Channels);
        Assert.Equal((BookingSource.BookingCom, 1, 100m), (row.Channel, row.Stays, row.LedgerGross));
        var statement = Assert.Single(result.Statements);
        Assert.Equal((65m, 0m), (statement.LedgerTotal, statement.PublishedTotal));
        Assert.DoesNotContain(result.Differences, d => d.OwnerId == stranger);
        Assert.DoesNotContain(result.Differences, d => d.Reason == OwnerReconciliationReasons.StayNotPosted);
        Assert.False(mine.Db.ChangeTracker.HasChanges());
        Assert.Equal(counts, (mine.Db.OwnerLedgerEntries.Count(), mine.Db.OwnerStatementVersions.Count(), mine.Db.Bookings.Count(), mine.Db.OwnerClosePeriods.Count()));

        // A month nothing happened in, and a month that does not exist.
        var quiet = await mine.Reconciliation.GetAsync(mine.OrgId, 2026, 9);
        Assert.Empty(quiet.Channels);
        Assert.Empty(quiet.Statements);
        Assert.Empty(quiet.Differences);
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => mine.Reconciliation.GetAsync(mine.OrgId, 2026, 13));
        Assert.Equal(OwnerAccountingErrors.PeriodInvalid, invalid.Code);
    }

    /// <summary>Permission: the reconciliation is the whole org's, a caller restricted to some properties is refused before anything is read; a caller of the whole org is served.</summary>
    [Fact]
    public async Task ReconciliationEndpoint_IsForACallerOfTheWholeOrgOnly_Test()
    {
        var org = Guid.NewGuid();
        var result = new OwnerReconciliation(2026, 10, [], [], []);
        var service = new Mock<IOwnerReconciliationService>(MockBehavior.Strict);
        service.Setup(s => s.GetAsync(org, 2026, 10, It.IsAny<CancellationToken>())).ReturnsAsync(result);

        var restricted = Controller(org, new HostScope(org, GrantedToUserId: "auth0|solo-alcuni"));
        Assert.IsType<ForbidResult>((await restricted.Reconciliation(2026, 10, service.Object)).Result);
        service.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        var wholeOrg = Controller(org, new HostScope(org));
        var served = Assert.IsType<OkObjectResult>((await wholeOrg.Reconciliation(2026, 10, service.Object)).Result);
        Assert.Same(result, served.Value);
        Assert.NotEmpty(typeof(OwnerAccountingController).GetMethod(nameof(OwnerAccountingController.Reconciliation))!.GetCustomAttributes(typeof(PrivateAnswerAttribute), false));
    }

    /// <summary>A payment recorded by hand that was refunded: part of it (<c>PartiallyRefunded</c>) or all of it (<c>Refunded</c>), as the refunds of CasaZen leave it.</summary>
    private static async Task RefundAsync(OwnerMonthEndWorld world, Guid paymentId, decimal refunded)
    {
        var payment = await world.Db.Payments.SingleAsync(p => p.Id == paymentId);
        payment.RefundedAmount = refunded;
        payment.Status = refunded >= payment.Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        await world.Db.SaveChangesAsync();
    }

    private static OwnerAccountingController Controller(Guid org, HostScope scope)
    {
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(org);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), org, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|agency"), new Claim(ClaimTypes.NameIdentifier, "auth0|agency")], "test");
        return new OwnerAccountingController(Mock.Of<IOwnerAccountingService>(), orgs.Object, scopes.Object, Mock.Of<IAuthorizationService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }
}
