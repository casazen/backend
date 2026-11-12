using System.Reflection;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs;
using Microsoft.Extensions.Configuration;
using Xunit;
using static Casazen.Tests.Unit.Services.MoneyReportsKit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-97 (E07/F03/US02/T01): the collections report is the Home's money cut up, never a second rule. For the same days the total is the "incassato" the Home
/// gives; a refund is deducted and a payment that failed, was canceled, is still open or was refunded in full is never counted; a balance to collect is the
/// total of a stay of CasaZen's against what was collected, and for a stay whose money is the channel manager's the balance Octorate reported, nothing else;
/// and no row ever says who the guest is. Numbers worked out by hand, October 2026 (the clock is 9 October, 10:00 UTC; Rome is on summer time until the 25th,
/// then one hour behind).
/// </summary>
public class HostCollectionsReportTests
{
    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly EndOfOctober = new(2026, 10, 31);

    private static HostCollectionsReportService Reports(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    private static HostDashboardService Home(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    private sealed record World(AppDbContext Db, Guid OrgId, Property Trullo, Property CasaBianca, Guest Guest);

    private static async Task<World> SeedAsync(Action<World>? more = null)
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-nf97", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-nf97", "Casa Bianca");
        var guest = NewGuest(orgId);
        db.Properties.AddRange(trullo, casaBianca);
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        var world = new World(db, orgId, trullo, casaBianca, guest);
        more?.Invoke(world);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return world;
    }

    private static Booking Add(World world, Property property, BookingStatus status, BookingSource source, DateTime checkIn, DateTime checkOut, decimal price, decimal? total = null)
    {
        var booking = Stay(property, world.Guest, status, source, checkIn, checkOut, price, total);
        world.Db.Bookings.Add(booking);
        return booking;
    }

    // ---------------------------------------------------------------- the money collected

    [Fact]
    public async Task Collections_TotalMatchesTheHomeCollected_Test()
    {
        Booking direct = null!, manual = null!, other = null!;
        var world = await SeedAsync(w =>
        {
            direct = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 2), Day(10, 5), 900m);
            manual = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Manual, Day(10, 2), Day(10, 5), 400m);
            other = Add(w, w.CasaBianca, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 9), Day(10, 12), 800m);
            var foreignProperty = HostScopeScenario.NewProperty(Guid.NewGuid(), "auth0|altro-nf97", "Villa Altrove");
            var foreignGuest = NewGuest(foreignProperty.OrgId, "Luca", "Neri");
            w.Db.Properties.Add(foreignProperty);
            w.Db.Guests.Add(foreignGuest);
            var foreign = Stay(foreignProperty, foreignGuest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 2), Day(10, 5), 999m);
            w.Db.Bookings.Add(foreign);

            w.Db.Payments.AddRange(
                // Counted: 300 + 200 + (500 - 120) + 80.10 + 60 = 1020.10, five payments.
                Pay(direct, 300m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(10, 3, 10)),
                Pay(manual, 200m, PaymentStatus.Completed, PaymentMethod.CashOnArrival, At(10, 3, 12)),
                Pay(other, 500m, PaymentStatus.PartiallyRefunded, PaymentMethod.BankTransfer, At(10, 10, 9), refunded: 120m),
                // 23:30 in Rome on the last day (22:30Z, the clocks went back on the 25th): the last minute of the period.
                Pay(other, 80.10m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(10, 31, 22, 30)),
                // Recorded before the instant of the payment existed: it counts on its creation day.
                Pay(manual, 60m, PaymentStatus.Completed, PaymentMethod.CashOnArrival, null, createdAt: At(10, 12, 8)),
                // Not counted: 00:30 in Rome on 1 November, a full refund, a failed one, a canceled one, two that are still open.
                Pay(direct, 40m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(10, 31, 23, 30)),
                Pay(direct, 300m, PaymentStatus.Refunded, PaymentMethod.CreditCard, At(10, 5, 9), refunded: 300m),
                Pay(direct, 200m, PaymentStatus.Failed, PaymentMethod.CreditCard, At(10, 6, 9)),
                Pay(direct, 200m, PaymentStatus.Canceled, PaymentMethod.CreditCard, At(10, 6, 9)),
                Pay(direct, 200m, PaymentStatus.Pending, PaymentMethod.BankTransfer, null),
                Pay(direct, 200m, PaymentStatus.Processing, PaymentMethod.CreditCard, At(10, 7, 9)),
                // Not in the period: the last minute of September in Rome, and another org's payment.
                Pay(direct, 1000m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(9, 30, 21, 59)),
                Pay(foreign, 999m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(10, 4, 9)));
        });
        var scope = new HostScope(world.OrgId);
        var query = new HostReportQuery(October, EndOfOctober);

        var report = await Reports(world.Db).GetCollectionsAsync(scope, query);
        var home = await Home(world.Db).GetKpisAsync(scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October, IncludeCollected: true));

        // The total is the Home's, to the cent and in the payments counted.
        Assert.Equal(new HostDashboardCollected(1020.10m, 5), report.Total);
        Assert.Equal(home.Collected, report.Total);

        // Every cut adds up to it.
        Assert.Equal(report.Total.Amount, report.ByDate.Sum(b => b.Amount));
        Assert.Equal(report.Total.PaymentCount, report.ByDate.Sum(b => b.PaymentCount));
        Assert.Equal(report.Total.Amount, report.ByMethod.Sum(m => m.Amount));
        Assert.Equal(report.Total.PaymentCount, report.ByMethod.Sum(m => m.PaymentCount));
        Assert.Equal(report.Total.Amount, report.ByChannel.Sum(c => c.Amount));
        Assert.Equal(report.Total.PaymentCount, report.ByChannel.Sum(c => c.PaymentCount));

        // By date: one bucket for each day of a period of 31 days, the days without a payment at zero; the two payments of the 3rd are one bucket.
        Assert.Equal(31, report.ByDate.Count);
        Assert.All(report.ByDate, b => Assert.Equal(HostCollectionsBucketUnit.Day, b.Unit));
        Assert.Equal(new DateOnly(2026, 10, 1), report.ByDate[0].From);
        Assert.Equal(new DateOnly(2026, 10, 31), report.ByDate[^1].To);
        Assert.Equal((500m, 2), (report.ByDate[2].Amount, report.ByDate[2].PaymentCount));
        Assert.Equal((60m, 1), (report.ByDate.Single(b => b.Label == "2026-10-12").Amount, report.ByDate.Single(b => b.Label == "2026-10-12").PaymentCount));
        Assert.Equal((80.10m, 1), (report.ByDate[^1].Amount, report.ByDate[^1].PaymentCount));
        Assert.Equal((0m, 0), (report.ByDate[0].Amount, report.ByDate[0].PaymentCount));

        // By method and by channel: the largest first, and a share that is the part of the total.
        Assert.Equal(
            new[] { (PaymentMethod.CreditCard, 380.10m, 2), (PaymentMethod.BankTransfer, 380m, 1), (PaymentMethod.CashOnArrival, 260m, 2) },
            report.ByMethod.Select(m => (m.Method, m.Amount, m.PaymentCount)).ToArray());
        Assert.Equal(
            new[] { (BookingSource.Direct, 760.10m, 3), (BookingSource.Manual, 260m, 2) },
            report.ByChannel.Select(c => (c.Source, c.Amount, c.PaymentCount)).ToArray());
        Assert.Equal(0.3726m, report.ByMethod[0].Share);

        // Cut by property, the report is the Home's for that property too.
        foreach (var property in new[] { world.Trullo, world.CasaBianca })
        {
            var ofProperty = await Reports(world.Db).GetCollectionsAsync(scope, query with { PropertyId = property.Id });
            var homeOfProperty = await Home(world.Db).GetKpisAsync(
                scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October, property.Id, IncludeCollected: true));
            Assert.Equal(homeOfProperty.Collected, ofProperty.Total);
        }
    }

    [Fact]
    public async Task Collections_TotalMatchesTheHomeCollected_AlsoForALongPeriodCutByMonth_Test()
    {
        Booking stay = null!;
        var world = await SeedAsync(w =>
        {
            stay = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 2), Day(10, 5), 900m);
            w.Db.Payments.AddRange(
                Pay(stay, 100m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(1, 15, 9)),
                // 31 January 23:30 UTC is 1 February in Rome: the payment is February's.
                Pay(stay, 50m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(1, 31, 23, 30)),
                Pay(stay, 70m, PaymentStatus.Completed, PaymentMethod.CashOnArrival, At(12, 31, 22, 30)));
        });
        var scope = new HostScope(world.OrgId);

        var report = await Reports(world.Db).GetCollectionsAsync(scope, new HostReportQuery(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        var home = await Home(world.Db).GetKpisAsync(
            scope,
            new HostDashboardQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 1, 1), To: new DateOnly(2026, 12, 31), IncludeCollected: true));

        Assert.Equal(new HostDashboardCollected(220m, 3), report.Total);
        Assert.Equal(home.Collected, report.Total);
        Assert.Equal(12, report.ByDate.Count);
        Assert.All(report.ByDate, b => Assert.Equal(HostCollectionsBucketUnit.Month, b.Unit));
        Assert.Equal((100m, 1), (report.ByDate[0].Amount, report.ByDate[0].PaymentCount));
        Assert.Equal((50m, 1), (report.ByDate[1].Amount, report.ByDate[1].PaymentCount));
        Assert.Equal((70m, 1), (report.ByDate[^1].Amount, report.ByDate[^1].PaymentCount));
        Assert.Equal(new DateOnly(2026, 2, 1), report.ByDate[1].From);
        Assert.Equal(new DateOnly(2026, 2, 28), report.ByDate[1].To);
    }

    [Fact]
    public async Task Collections_RefundsDeductedAndFailedIgnored_Test()
    {
        Booking stay = null!;
        var world = await SeedAsync(w =>
        {
            stay = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 2), Day(10, 5), 900m);
            w.Db.Payments.AddRange(
                // A partial refund is deducted: 500 less 120 brings 380; a payment refunded in full brings nothing, and never counts as a payment.
                Pay(stay, 500m, PaymentStatus.PartiallyRefunded, PaymentMethod.CreditCard, At(10, 3, 9), refunded: 120m),
                Pay(stay, 300m, PaymentStatus.Refunded, PaymentMethod.CreditCard, At(10, 3, 9), refunded: 300m),
                // Never collected: failed, canceled, still open.
                Pay(stay, 200m, PaymentStatus.Failed, PaymentMethod.CreditCard, At(10, 3, 9)),
                Pay(stay, 200m, PaymentStatus.Canceled, PaymentMethod.CreditCard, At(10, 3, 9)),
                Pay(stay, 200m, PaymentStatus.Pending, PaymentMethod.CreditCard, null),
                Pay(stay, 200m, PaymentStatus.Processing, PaymentMethod.CreditCard, At(10, 3, 9)),
                // A refund larger than the payment (a data mistake) never makes the money collected negative.
                Pay(stay, 100m, PaymentStatus.PartiallyRefunded, PaymentMethod.CashOnArrival, At(10, 4, 9), refunded: 130m));
        });

        var report = await Reports(world.Db).GetCollectionsAsync(new HostScope(world.OrgId), new HostReportQuery(October, EndOfOctober));

        Assert.Equal(new HostDashboardCollected(380m, 2), report.Total);
        Assert.Equal(new[] { (PaymentMethod.CreditCard, 380m), (PaymentMethod.CashOnArrival, 0m) }, report.ByMethod.Select(m => (m.Method, m.Amount)).ToArray());
        Assert.Equal(380m, report.ByDate.Sum(b => b.Amount));
        Assert.Equal(1m, report.ByMethod.Sum(m => m.Share));
    }

    [Fact]
    public async Task Collections_WithNothingCollected_IsAnEmptyReport_NotAnError_AndHasNoShare()
    {
        var world = await SeedAsync();

        var report = await Reports(world.Db).GetCollectionsAsync(new HostScope(world.OrgId), new HostReportQuery(October, EndOfOctober));

        Assert.Equal(new HostDashboardCollected(0m, 0), report.Total);
        Assert.Empty(report.ByMethod);
        Assert.Empty(report.ByChannel);
        Assert.Equal(31, report.ByDate.Count);
        Assert.All(report.ByDate, b => Assert.Equal((0m, 0), (b.Amount, b.PaymentCount)));
        Assert.False(report.Credits.Stripe.Available);
        Assert.False(report.Credits.Channels.Available);
    }

    // ---------------------------------------------------------------- reach

    [Fact]
    public async Task Collections_AndBalances_ShowOnlyWhatTheScopeReaches_AndAPropertyOutsideItIsAnother404()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);

        var restrictedCollections = await Reports(db).GetCollectionsAsync(world.Restricted, new HostReportQuery(October, EndOfOctober));
        var orgWideCollections = await Reports(db).GetCollectionsAsync(world.OrgWide, new HostReportQuery(October, EndOfOctober));
        var restrictedBalances = await Reports(db).GetBalancesToCollectAsync(world.Restricted, new HostBalancesQuery());
        var orgWideBalances = await Reports(db).GetBalancesToCollectAsync(world.OrgWide, new HostBalancesQuery());

        // Each property of the world has two payments of 300 on 5 October: the collaborator sees the granted property's alone, the org both, nobody the other org's.
        Assert.Equal(new HostDashboardCollected(600m, 2), restrictedCollections.Total);
        Assert.Equal(new HostDashboardCollected(1200m, 4), orgWideCollections.Total);
        // Each property has a request waiting for the host (300, no payment): a balance to collect.
        HostScopePoints.OnlyTheGranted(world, restrictedBalances.Rows.Select(r => r.PropertyId), "balances to collect");
        HostScopePoints.TheWholeOrgButNotAnotherOne(world, orgWideBalances.Rows.Select(r => r.PropertyId), "balances to collect");
        Assert.Equal(300m, Assert.Single(restrictedBalances.Totals).Amount);

        // One 404 for a property that is not the caller's, whoever it belongs to and whatever it is called.
        foreach (var propertyId in new[] { world.Hidden.Id, world.OtherOrgProperty.Id, Guid.NewGuid() })
        {
            var collections = await Assert.ThrowsAsync<NotFoundException>(
                () => Reports(db).GetCollectionsAsync(world.Restricted, new HostReportQuery(October, EndOfOctober, propertyId)));
            var balances = await Assert.ThrowsAsync<NotFoundException>(
                () => Reports(db).GetBalancesToCollectAsync(world.Restricted, new HostBalancesQuery(propertyId)));
            Assert.Equal("PropertyNotFound", collections.MessageKey);
            Assert.Equal("PropertyNotFound", balances.MessageKey);
        }

        // The whole org reaches its own properties, not another org's.
        await Assert.ThrowsAsync<NotFoundException>(
            () => Reports(db).GetCollectionsAsync(world.OrgWide, new HostReportQuery(October, EndOfOctober, world.OtherOrgProperty.Id)));
        var one = await Reports(db).GetCollectionsAsync(world.OrgWide, new HostReportQuery(October, EndOfOctober, world.Hidden.Id));
        Assert.Equal(new HostDashboardCollected(600m, 2), one.Total);
    }

    // ---------------------------------------------------------------- the credits

    [Fact]
    public async Task Credits_StripeFiguresNotStored_AreNotAvailable_NeverZero()
    {
        var world = await SeedAsync(w =>
        {
            var stay = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 2), Day(10, 5), 900m);
            w.Db.Payments.AddRange(
                CardPay(stay, 300m, At(10, 3, 9), null, null, null),
                CardPay(stay, 200m, At(10, 4, 9), null, null, null),
                // A cash payment is no card payment: it is not waiting for figures.
                Pay(stay, 100m, PaymentStatus.Completed, PaymentMethod.CashOnArrival, At(10, 4, 9)));
        });

        var report = await Reports(world.Db).GetCollectionsAsync(new HostScope(world.OrgId), new HostReportQuery(October, EndOfOctober));

        var stripe = report.Credits.Stripe;
        Assert.False(stripe.Available);
        Assert.Empty(stripe.Payouts);
        Assert.Equal((2, 2), (stripe.CardPaymentCount, stripe.PaymentsWithoutFigures));
    }

    [Fact]
    public async Task Credits_StripeFiguresStored_AreGroupedByTheDayTheFundsArrive_WithTheirStatus()
    {
        var world = await SeedAsync(w =>
        {
            var stay = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 2), Day(10, 5), 900m);
            var other = Add(w, w.CasaBianca, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 2), Day(10, 5), 900m);
            w.Db.Payments.AddRange(
                // Two payments credited on the 8th (before today, the 9th), one on the 12th (still ahead), one in another currency on the 8th.
                CardPay(stay, 300m, At(10, 6, 9), 4.75m, 295.25m, At(10, 8, 0)),
                CardPay(other, 200m, At(10, 6, 14), 3.25m, 196.75m, At(10, 8, 0)),
                CardPay(stay, 100m, At(10, 9, 8), 1.75m, 98.25m, At(10, 12, 0)),
                CardPay(stay, 50m, At(10, 6, 15), 1m, 49m, At(10, 8, 0), "CHF"),
                // A payment refunded in full was credited all the same: it is not collected any more, but the funds arrived.
                CardPay(stay, 80m, At(10, 6, 16), 1.45m, 78.55m, At(10, 8, 0), status: PaymentStatus.Refunded),
                // Without figures it is counted, not shown; outside the period it is not in the credits at all; a failed payment never settled.
                CardPay(stay, 60m, At(10, 7, 9), null, null, null),
                CardPay(stay, 40m, At(9, 20, 9), 1m, 39m, At(9, 22, 0)),
                CardPay(stay, 30m, At(10, 7, 10), 1m, 29m, At(10, 9, 0), status: PaymentStatus.Failed));
        });

        var report = await Reports(world.Db).GetCollectionsAsync(new HostScope(world.OrgId), new HostReportQuery(October, EndOfOctober));

        var stripe = report.Credits.Stripe;
        Assert.True(stripe.Available);
        Assert.Equal(
            new (DateOnly, string, decimal, int, HostCreditStatus)[]
            {
                (new DateOnly(2026, 10, 8), "CHF", 49m, 1, HostCreditStatus.Credited),
                (new DateOnly(2026, 10, 8), "EUR", 570.55m, 3, HostCreditStatus.Credited),
                (new DateOnly(2026, 10, 12), "EUR", 98.25m, 1, HostCreditStatus.Expected),
            },
            stripe.Payouts.Select(p => (p.ArrivalDate, p.Currency, p.Amount, p.PaymentCount, p.Status)).ToArray());
        // Collected in the period: the five completed card payments the report counts (the refunded one and the failed one do not) and one without figures.
        Assert.Equal((5, 1), (stripe.CardPaymentCount, stripe.PaymentsWithoutFigures));

        // Cut to one property the credits are the property's.
        var ofCasaBianca = await Reports(world.Db).GetCollectionsAsync(
            new HostScope(world.OrgId), new HostReportQuery(October, EndOfOctober, world.CasaBianca.Id));
        var row = Assert.Single(ofCasaBianca.Credits.Stripe.Payouts);
        Assert.Equal((new DateOnly(2026, 10, 8), 196.75m, 1), (row.ArrivalDate, row.Amount, row.PaymentCount));
    }

    [Fact]
    public async Task Credits_OfTheChannels_AreOnlyWhatTheChannelCollectedAndTheChannelManagerReportedANetFor()
    {
        var world = await SeedAsync(w =>
        {
            Booking Channel(BookingSource source, DateTime checkIn, string reservation, bool? companyCollect, decimal? net, BookingStatus status = BookingStatus.Confirmed)
            {
                var booking = Add(w, w.Trullo, status, source, checkIn, checkIn.AddDays(2), 500m);
                booking.OctorateReservationId = reservation;
                booking.OtaCompanyCollect = companyCollect;
                booking.OtaNetAmount = net;
                return booking;
            }

            Channel(BookingSource.Airbnb, Day(10, 3), "RES-1", true, 440.50m);
            Channel(BookingSource.Airbnb, Day(10, 10), "RES-2", true, 300m);
            Channel(BookingSource.Airbnb, Day(10, 17), "RES-3", true, null);
            Channel(BookingSource.BookingCom, Day(10, 4), "RES-4", true, 200m);
            // Not the channel's money, or not said, or not a stay, or not in the days: none of them is a channel credit.
            Channel(BookingSource.BookingCom, Day(10, 5), "RES-5", false, 999m);
            Channel(BookingSource.Expedia, Day(10, 6), "RES-6", null, 999m);
            Channel(BookingSource.Vrbo, Day(10, 7), "RES-7", true, 999m, BookingStatus.Cancelled);
            Channel(BookingSource.Airbnb, Day(11, 3), "RES-8", true, 999m);
        });

        var report = await Reports(world.Db).GetCollectionsAsync(new HostScope(world.OrgId), new HostReportQuery(October, EndOfOctober));

        var channels = report.Credits.Channels;
        Assert.True(channels.Available);
        Assert.Equal(
            new[] { (BookingSource.Airbnb, "EUR", 740.50m, 2), (BookingSource.BookingCom, "EUR", 200m, 1) },
            channels.Rows.Select(r => (r.Source, r.Currency, r.Amount, r.StayCount)).ToArray());
        Assert.Equal(1, channels.StaysWithoutNet);
    }

    [Fact]
    public async Task Credits_OfTheChannels_WithoutAReportedNet_AreNotAvailable_NeverZero()
    {
        var world = await SeedAsync(w =>
        {
            var booking = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Airbnb, Day(10, 3), Day(10, 5), 500m);
            booking.OctorateReservationId = "RES-1";
            booking.OtaCompanyCollect = true;
        });

        var report = await Reports(world.Db).GetCollectionsAsync(new HostScope(world.OrgId), new HostReportQuery(October, EndOfOctober));

        Assert.False(report.Credits.Channels.Available);
        Assert.Empty(report.Credits.Channels.Rows);
        Assert.Equal(1, report.Credits.Channels.StaysWithoutNet);
    }

    // ---------------------------------------------------------------- the balances

    [Fact]
    public async Task BalancesToCollect_OtaUsesTheChannelBalance_Test()
    {
        Dictionary<string, Booking> by = new();
        var world = await SeedAsync(w =>
        {
            Booking Make(string key, Property property, BookingStatus status, BookingSource source, DateTime checkIn, DateTime checkOut, decimal total)
            {
                var booking = Add(w, property, status, source, checkIn, checkOut, total);
                by[key] = booking;
                return booking;
            }

            Booking Channel(string key, BookingSource source, DateTime checkIn, string? reservation, decimal total, decimal? balance, decimal? paid, bool? companyCollect)
            {
                var booking = Make(key, w.Trullo, BookingStatus.Confirmed, source, checkIn, checkIn.AddDays(3), total);
                booking.OctorateReservationId = reservation;
                booking.OtaBalanceAmount = balance;
                booking.OtaPaidAmount = paid;
                booking.OtaCompanyCollect = companyCollect;
                return booking;
            }

            // CasaZen's money: 500 against 200 + 60 collected (a failed, an open and a canceled payment are nothing, a partial refund is deducted).
            var direct = Make("direct", w.Trullo, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 12), Day(10, 15), 500m);
            w.Db.Payments.AddRange(
                Pay(direct, 200m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(10, 2, 9)),
                Pay(direct, 150m, PaymentStatus.Failed, PaymentMethod.CreditCard, At(10, 2, 9)),
                Pay(direct, 100m, PaymentStatus.Pending, PaymentMethod.BankTransfer, null),
                Pay(direct, 90m, PaymentStatus.Canceled, PaymentMethod.CreditCard, At(10, 2, 9)),
                Pay(direct, 100m, PaymentStatus.PartiallyRefunded, PaymentMethod.CashOnArrival, At(10, 3, 9), refunded: 40m));
            var paidInFull = Make("paid", w.Trullo, BookingStatus.Confirmed, BookingSource.Manual, Day(10, 5), Day(10, 8), 300m);
            w.Db.Payments.Add(Pay(paidInFull, 300m, PaymentStatus.Completed, PaymentMethod.BankTransfer, At(10, 1, 9)));
            var overdue = Make("overdue", w.Trullo, BookingStatus.CheckedOut, BookingSource.Manual, Day(10, 1), Day(10, 4), 400m);
            w.Db.Payments.Add(Pay(overdue, 100m, PaymentStatus.Completed, PaymentMethod.CashOnArrival, At(10, 1, 9)));
            var request = Make("request", w.Trullo, BookingStatus.Pending, BookingSource.Direct, Day(10, 25), Day(10, 27), 250m);
            request.PaymentOption = PaymentOption.OnSite;
            request.RequestExpiresAt = At(10, 10, 10);
            Make("other-property", w.CasaBianca, BookingStatus.Confirmed, BookingSource.Local, Day(10, 11), Day(10, 12), 100m);

            // The channel manager's money: the balance of the reservation, when the host collects it.
            Channel("ota-balance", BookingSource.BookingCom, Day(10, 20), "RES-1", 200m, 120m, 80m, false);
            Channel("ota-balance-unsaid", BookingSource.Expedia, Day(10, 22), "RES-9", 300m, 45m, null, null);
            Channel("ota-channel-collects", BookingSource.Airbnb, Day(10, 21), "RES-2", 400m, 90m, 310m, true);
            Channel("ota-unreported", BookingSource.Expedia, Day(10, 23), "RES-3", 400m, null, null, false);
            Channel("ota-nothing-left", BookingSource.Vrbo, Day(10, 24), "RES-4", 400m, 0m, 400m, false);
            // A stay of a channel without a reservation (an iCal stay) is the channel's own record: its price is not a balance.
            var ical = Make("ota-ical", w.Trullo, BookingStatus.Confirmed, BookingSource.Airbnb, Day(10, 26), Day(10, 28), 300m);
            ical.OctorateReservationId = null;
            // The booking engine of the agency (a Direct reservation the import made): its money is the channel manager's, so only a reported balance counts.
            Channel("engine-balance", BookingSource.Direct, Day(10, 16), "RES-5", 300m, 70m, 230m, false);
            Channel("engine-unreported", BookingSource.Direct, Day(10, 17), "RES-6", 300m, null, null, null);
            // A Direct stay CasaZen sent to Octorate has a reservation too, and a row of the mirror: its money is CasaZen's.
            var pushed = Channel("pushed", BookingSource.Direct, Day(10, 14), "RES-7", 600m, null, null, null);
            w.Db.OctorateBookingSyncs.Add(new OctorateBookingSync { OrgId = w.OrgId, BookingId = pushed.Id, PropertyId = w.Trullo.Id, State = OctorateSyncState.Synced });
            w.Db.Payments.Add(Pay(pushed, 100m, PaymentStatus.Completed, PaymentMethod.CreditCard, At(10, 3, 9)));

            // Stays that hold no dates: cancelled, and a checkout hold that expired (a card payment left open for hours).
            Make("cancelled", w.Trullo, BookingStatus.Cancelled, BookingSource.Direct, Day(10, 18), Day(10, 19), 500m);
            var expired = Make("expired-hold", w.Trullo, BookingStatus.Pending, BookingSource.Direct, Day(10, 29), Day(10, 31), 500m);
            expired.StripeSetupIntentId = "seti_demo";
            expired.CreatedAt = At(10, 9, 7);

            var foreignProperty = HostScopeScenario.NewProperty(Guid.NewGuid(), "auth0|altro-nf97", "Villa Altrove");
            var foreignGuest = NewGuest(foreignProperty.OrgId, "Luca", "Neri");
            w.Db.Properties.Add(foreignProperty);
            w.Db.Guests.Add(foreignGuest);
            w.Db.Bookings.Add(Stay(foreignProperty, foreignGuest, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 12), Day(10, 14), 999m));
        });

        var report = await Reports(world.Db).GetBalancesToCollectAsync(new HostScope(world.OrgId), new HostBalancesQuery());

        // As of today in Rome, by check-in; only the stays with something to collect.
        Assert.Equal(new DateOnly(2026, 10, 9), report.AsOf);
        var rows = report.Rows.ToDictionary(r => r.BookingId);
        Assert.Equal(
            new[] { "overdue", "other-property", "direct", "pushed", "engine-balance", "ota-balance", "ota-balance-unsaid", "request" }.Order(),
            rows.Keys.Select(id => by.Single(kv => kv.Value.Id == id).Key).Order());
        Assert.Equal(report.Rows.Select(r => r.CheckIn).Order(), report.Rows.Select(r => r.CheckIn));

        HostBalanceRow Row(string key) => rows[by[key].Id];

        // CasaZen's money: the total minus what was collected (200 + 60), and what is not collected is nothing.
        Assert.Equal((500m, 260m, 240m, false, HostBalanceBasis.Payments), (Row("direct").Total, Row("direct").Collected, Row("direct").ToCollect, Row("direct").Overdue, Row("direct").Basis));
        Assert.Equal((400m, 100m, 300m, true), (Row("overdue").Total, Row("overdue").Collected, Row("overdue").ToCollect, Row("overdue").Overdue));
        Assert.Equal((250m, 0m, 250m), (Row("request").Total, Row("request").Collected, Row("request").ToCollect));
        Assert.Equal((600m, 100m, 500m, HostBalanceBasis.Payments), (Row("pushed").Total, Row("pushed").Collected, Row("pushed").ToCollect, Row("pushed").Basis));
        Assert.Equal(100m, Row("other-property").ToCollect);

        // The channel manager's money: the balance it reported, as stored, and the paid amount as stored (null stays null, never worked out).
        Assert.Equal((200m, 80m, 120m, HostBalanceBasis.ChannelManager), (Row("ota-balance").Total, Row("ota-balance").Collected, Row("ota-balance").ToCollect, Row("ota-balance").Basis));
        Assert.Equal((300m, (decimal?)null, 45m), (Row("ota-balance-unsaid").Total, Row("ota-balance-unsaid").Collected, Row("ota-balance-unsaid").ToCollect));
        Assert.Equal((300m, 230m, 70m, HostBalanceBasis.ChannelManager), (Row("engine-balance").Total, Row("engine-balance").Collected, Row("engine-balance").ToCollect, Row("engine-balance").Basis));

        // Never a row: paid in full, a channel that collects, a balance never reported (or zero), an iCal stay, a cancelled stay, an expired hold, another org.
        foreach (var key in new[] { "paid", "ota-channel-collects", "ota-unreported", "ota-nothing-left", "ota-ical", "engine-unreported", "cancelled", "expired-hold" })
            Assert.DoesNotContain(by[key].Id, rows.Keys);

        // What is to collect, in the one currency of the properties.
        var total = Assert.Single(report.Totals);
        Assert.Equal(("EUR", 240m + 300m + 250m + 500m + 100m + 120m + 45m + 70m, 8), (total.Currency, total.Amount, total.StayCount));
    }

    [Fact]
    public async Task BalancesToCollect_AHouseInFrancs_IsNeverAddedToTheEuros()
    {
        Booking euro = null!, franc = null!;
        var world = await SeedAsync(w =>
        {
            w.CasaBianca.Currency = "CHF";
            euro = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Manual, Day(10, 12), Day(10, 14), 100m);
            franc = Add(w, w.CasaBianca, BookingStatus.Confirmed, BookingSource.Manual, Day(10, 12), Day(10, 14), 200m);
        });

        var report = await Reports(world.Db).GetBalancesToCollectAsync(new HostScope(world.OrgId), new HostBalancesQuery());

        Assert.Equal(new[] { ("CHF", 200m, 1), ("EUR", 100m, 1) }, report.Totals.Select(t => (t.Currency, t.Amount, t.StayCount)).ToArray());
        Assert.Equal("CHF", report.Rows.Single(r => r.BookingId == franc.Id).Currency);
        Assert.Equal("EUR", report.Rows.Single(r => r.BookingId == euro.Id).Currency);
    }

    [Fact]
    public async Task BalancesToCollect_NoGuestData_Test()
    {
        var world = await SeedAsync(w =>
        {
            w.Guest.FirstName = "Anna";
            w.Guest.LastName = "Verdi-Segretissima";
            w.Guest.Email = "anna.segretissima@example.com";
            w.Guest.PhoneNumber = "+39 333 7654321";
            var direct = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.Direct, Day(10, 12), Day(10, 15), 500m);
            direct.SpecialRequests = "Allergica ai gatti, arriva con la sorella";
            var channel = Add(w, w.Trullo, BookingStatus.Confirmed, BookingSource.BookingCom, Day(10, 20), Day(10, 22), 300m);
            channel.OctorateReservationId = "RES-1";
            channel.OtaBalanceAmount = 100m;
            channel.OtaCompanyCollect = false;
        });

        var report = await Reports(world.Db).GetBalancesToCollectAsync(new HostScope(world.OrgId), new HostBalancesQuery());
        var json = JsonSerializer.Serialize(BalancesToCollectReportDto.From(report, HostCollectionsReportDocuments.BalancesTitle));
        var csv = System.Text.Encoding.UTF8.GetString(HostCollectionsReportDocuments.Csv(report));

        // Rows were produced (the proof is not about an empty answer) and say nothing of the guest, in the data, in the JSON or in the file.
        Assert.Equal(2, report.Rows.Count);
        foreach (var text in new[] { json, csv })
        {
            foreach (var secret in new[] { "Segretissima", "anna.segretissima", "example.com", "7654321", "gatti", "sorella" })
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        // The shape of a row has no guest: the fields it has are the ones of a stay's money, nothing that names or reaches a person.
        var expected = new[]
        {
            "BookingId", "BookingCode", "PropertyId", "PropertyName", "Source", "CheckIn", "CheckOut", "Currency", "Total", "Collected", "ToCollect", "Overdue", "Basis",
        };
        Assert.Equal(expected.Order(), typeof(HostBalanceRow).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order());
        Assert.Equal(expected.Order(), typeof(BalanceRowDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Where(n => !n.EndsWith("Cents", StringComparison.Ordinal)).Order());
    }
}
