using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Demo;

/// <summary>
/// What the showcase writes for the money reports (E07/F03/US02, NF-97, NF-98) is valid for the real rules: the payments of the demo stays are written by the real
/// seeding code, marked as showcase data, say TEST, are never in the future and have the figures the provider would report; and the real reports read them and
/// agree with the template, at any time of the day the showcase is reset.
/// </summary>
public class DemoCollectionsSeedTests
{
    private const string HouseKey = "villa-lago";

    [Theory]
    [InlineData("2026-11-06T00:30:00Z")]
    [InlineData("2026-11-06T10:00:00Z")]
    [InlineData("2026-11-06T23:30:00Z")]
    public async Task ReportCollections_TheSeededPayments_AreShowcaseDataThatSaysTest_AndTheReportsReadThem(string now)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));
        var world = await SeedAsync(clock);
        var today = RomeToday(clock);
        var template = DemoShowcaseCatalog.Properties.Single(p => p.Key == HouseKey);
        var templated = template.Bookings.SelectMany(b => (b.Payments ?? []).Select(p => (Booking: b, Payment: p))).ToList();

        // Every payment of the template is a row, marked as showcase data, that says TEST, never in the future.
        var payments = await world.Db.Payments.AsNoTracking().ToListAsync();
        Assert.Equal(templated.Count, payments.Count);
        var markers = await world.Db.TestDataMarkers.AsNoTracking().Where(m => m.EntityType == TestDataEntities.Payment).ToListAsync();
        Assert.Equal(payments.Select(p => p.Id).Order(), markers.Select(m => m.EntityId).Order());
        Assert.All(markers, m => Assert.Equal(TestDataOrigins.Showcase, m.Origin));
        var instant = clock.GetUtcNow().UtcDateTime;
        Assert.All(payments, p =>
        {
            Assert.StartsWith("TEST-", p.TransactionId, StringComparison.Ordinal);
            Assert.Contains("TEST", p.Description, StringComparison.Ordinal);
            Assert.True(p.CreatedAt <= instant && (p.ProcessedAt ?? p.CreatedAt) <= instant, "A payment of the showcase never comes in the future.");
            Assert.Equal(p.ProcessedAt is not null, p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded);
        });

        // A card payment the provider settled has a TEST intent and the figures the capture stores, already read (so the capture never asks Stripe for it); a payment
        // that never went through the provider has none of them.
        var cards = payments.Where(p => p.StripePaymentIntentId is not null).ToList();
        Assert.Equal(templated.Count(t => t.Payment.Card is not null), cards.Count);
        Assert.All(cards, p =>
        {
            Assert.StartsWith("pi_TEST_", p.StripePaymentIntentId, StringComparison.Ordinal);
            Assert.Equal(p.Amount, p.ProviderFeeAmount + p.ProviderNetAmount);
            Assert.Equal("EUR", p.ProviderBalanceCurrency);
            Assert.NotNull(p.ProviderAvailableOn);
            Assert.NotNull(p.ProviderFeeFetchedAt);
        });
        Assert.All(payments.Where(p => p.StripePaymentIntentId is null), p => Assert.Null(p.ProviderNetAmount));

        // The collections report reads them: the Home's rule, over every day the template has.
        var reports = new HostCollectionsReportService(world.Db, new ConfigurationBuilder().Build(), clock);
        var scope = new HostScope(world.Org.Id);
        var collected = templated.Where(t => t.Payment.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded).ToList();
        var report = await reports.GetCollectionsAsync(scope, new HostReportQuery(today.AddDays(-100), today.AddDays(10)));

        Assert.Equal(new HostDashboardCollected(collected.Sum(t => t.Payment.Amount - t.Payment.RefundedAmount), collected.Count), report.Total);
        Assert.Equal(report.Total.Amount, report.ByMethod.Sum(m => m.Amount));
        Assert.Equal(report.Total.Amount, report.ByChannel.Sum(c => c.Amount));
        Assert.Contains(report.ByMethod, m => m.Method == PaymentMethod.CashOnArrival);
        Assert.Contains(report.ByMethod, m => m.Method == PaymentMethod.BankTransfer);
        Assert.Contains(report.ByMethod, m => m.Method == PaymentMethod.CreditCard);

        // The credits of the card provider: what the template says it credited, on the days it says, credited or expected as the day stands.
        var expectedCredits = collected
            .Where(t => t.Payment.Card is not null)
            .GroupBy(t => today.AddDays(-t.Payment.DaysAgo + t.Payment.Card!.CreditAfterDays))
            .OrderBy(g => g.Key)
            .Select(g => (Day: g.Key, Net: g.Sum(t => t.Payment.Card!.Net), Count: g.Count(), Credited: g.Key <= today))
            .ToArray();
        Assert.True(report.Credits.Stripe.Available);
        Assert.Equal(
            expectedCredits,
            report.Credits.Stripe.Payouts.Select(p => (Day: p.ArrivalDate, Net: p.Amount, Count: p.PaymentCount, Credited: p.Status == HostCreditStatus.Credited)).ToArray());
        Assert.Equal(0, report.Credits.Stripe.PaymentsWithoutFigures);

        // The balances: what the template leaves to collect, stay by stay (a cancelled stay, a stay of a portal without a reservation and a balance of zero are none).
        var balances = await reports.GetBalancesToCollectAsync(scope, new HostBalancesQuery());
        var expected = template.Bookings
            .Select(b => (b.Key, ToCollect: ExpectedToCollect(template, b)))
            .Where(x => x.ToCollect is not null)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => (x.Key, x.ToCollect!.Value))
            .ToArray();
        var keyOf = await world.Db.TestDataMarkers.AsNoTracking()
            .Where(m => m.EntityType == TestDataEntities.Booking)
            .ToDictionaryAsync(m => m.EntityId, m => m.TemplateKey!);
        Assert.Equal(expected, balances.Rows.Select(r => (keyOf[r.BookingId], r.ToCollect)).OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray());
        Assert.Contains(balances.Rows, r => r.Basis == HostBalanceBasis.ChannelManager && r.Overdue && r.ToCollect == 380m);
        Assert.Contains(balances.Rows, r => r.Basis == HostBalanceBasis.Payments && !r.Overdue && r.ToCollect == 380m);

        // The payments go with their stays at the next reset: the table cascades (the reset deletes the stays, and the model deletes what hangs on them).
        world.Db.Bookings.RemoveRange(await world.Db.Bookings.Include(b => b.Payments).ToListAsync());
        await world.Db.SaveChangesAsync();
        Assert.Empty(await world.Db.Payments.ToListAsync());
    }

    [Fact]
    public async Task ReportChannelCosts_TheSeededStays_ShowTwoChannelsAStripeFeeAndAStayWithoutACommission_CountedApart()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        var world = await SeedAsync(clock);
        var today = RomeToday(clock);
        var modes = new Dictionary<BookingSource, ChannelCommissionMode>
        {
            [BookingSource.Airbnb] = ChannelCommissionMode.DeductedFromPayout,
            [BookingSource.BookingCom] = ChannelCommissionMode.InvoicedSeparately,
        };

        var report = await new ChannelCostsReportService(world.Db, new Modes(modes))
            .GetChannelCostsAsync(new HostScope(world.Org.Id), new HostReportQuery(today.AddDays(-40), today.AddDays(-1)));

        // The channels, as the channel manager reported them and the portals settle them: Airbnb 12 % deducted from the payout, Booking.com 15 % invoiced apart.
        var airbnb = report.Rows.Single(r => r.Source == BookingSource.Airbnb);
        Assert.Equal((1, 580m, 69.60m, 510.40m, 0.12m, 0), (airbnb.StayCount, airbnb.Gross, airbnb.Commission, airbnb.Net, airbnb.CostRate, airbnb.StaysWithoutCommission));
        Assert.Equal(ChannelCommissionMode.DeductedFromPayout, airbnb.CommissionMode);

        // The Booking.com stay of the collections scenario has no commission reported: it is counted apart, and the rate stands on the stay that has one.
        var booking = report.Rows.Single(r => r.Source == BookingSource.BookingCom);
        Assert.Equal((2, 1160m, 87.00m, 493.00m, 0.15m, 1), (booking.StayCount, booking.Gross, booking.Commission, booking.Net, booking.CostRate, booking.StaysWithoutCommission));
        Assert.Equal(ChannelCommissionMode.InvoicedSeparately, booking.CommissionMode);
        Assert.True(booking.MostExpensive);

        // The booking site: the fees its card payments stored (400 and 580 of gross), the stays that are not charged by anyone cost nothing.
        var direct = report.Rows.Single(r => r.Source == BookingSource.Direct);
        Assert.Equal((2, 980m, 15.20m, 964.80m, 0.0155m, 0), (direct.StayCount, direct.Gross, direct.Commission, direct.Net, direct.CostRate, direct.StaysWithoutCommission));
        Assert.Equal(ChannelCostKind.PaymentFee, direct.Kind);
        Assert.Equal(ChannelCostKind.NoCost, report.Rows.Single(r => r.Source == BookingSource.Manual).Kind);
        Assert.Equal(ChannelCostKind.NoCost, report.Rows.Single(r => r.Source == BookingSource.Local).Kind);

        // The Home's revenue of the same days, and the costs that are known.
        Assert.Equal((3980m, 171.80m, 3228.20m, 0.0505m, 1), (report.Total.Gross, report.Total.Commission, report.Total.Net, report.Total.CostRate, report.Total.StaysWithoutCommission));
    }

    private sealed class Modes(Dictionary<BookingSource, ChannelCommissionMode> modes) : IChannelCommissionSettingsProvider
    {
        public Task<IReadOnlyDictionary<BookingSource, ChannelCommissionMode>> GetModesAsync(Guid orgId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<BookingSource, ChannelCommissionMode>>(modes);
    }

    // What the template leaves to collect on a stay, worked out from the template alone (the rule of the report, written again to check it).
    private static decimal? ExpectedToCollect(DemoPropertyTemplate house, DemoBookingTemplate booking)
    {
        if (booking.Status == BookingStatus.Cancelled)
            return null;

        var channelMoney = booking.Ota is not null && (FiscalCopy.IsOtaBookingSource(booking.Source) || (booking.Source == BookingSource.Direct && booking.Ota.Sync is null));
        if (channelMoney)
            return booking.Ota!.Balance is > 0m && booking.Ota.CompanyCollect != true ? booking.Ota.Balance : null;

        if (FiscalCopy.IsOtaBookingSource(booking.Source))
            return null;

        var total = (booking.AgreedTotal ?? (house.NightlyRate * booking.Nights) + house.CleaningFee) + (booking.TouristTax ?? 0m);
        var collected = (booking.Payments ?? [])
            .Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded)
            .Sum(p => PaymentCashRules.Collected(p.Amount, p.RefundedAmount));
        return total > collected ? total - collected : null;
    }

    private static DateOnly RomeToday(FakeTimeProvider clock) => Casazen.Core.Utilities.RomeCalendar.TodayInRomeAsDateOnly(clock);

    private sealed record SeedWorld(AppDbContext Db, OrgEntity Org);

    /// <summary>An org with its owner and the house of the catalog that has the payments, seeded by the real code of the showcase, over an InMemory context.</summary>
    private static async Task<SeedWorld> SeedAsync(FakeTimeProvider clock)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Host", $"host-{suffix}");
        var owner = HostScopeScenario.NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Orgs.Add(org);
        db.Users.Add(owner);

        var template = DemoShowcaseCatalog.Properties.Single(p => p.Key == HouseKey);
        var house = HostScopeScenario.NewProperty(org.Id, owner.Id, template.Name);
        house.NightlyRate = template.NightlyRate;
        house.CleaningFee = template.CleaningFee;
        db.Properties.Add(house);
        await db.SaveChangesAsync();

        var gate = new Mock<IDemoEnvironmentGate>();
        gate.SetupGet(g => g.IsAvailable).Returns(true);
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var seeding = new DemoEnvironmentService(
            db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IPropertyComplianceStatusService>(),
            Microsoft.Extensions.Options.Options.Create(new ComplianceOptions()),
            gate.Object,
            NullLogger<DemoEnvironmentService>.Instance,
            clock,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<ICrmContactIndex>(),
            Mock.Of<IFeatureFlags>(),
            hostEnvironment.Object);

        var today = RomeToday(clock);
        foreach (var booking in template.Bookings)
            await seeding.CreateBookingAsync(org.Id, house, booking, today, CancellationToken.None);

        return new SeedWorld(db, org);
    }
}
