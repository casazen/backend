using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.MoneyReportsKit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-98 (E07/F03/US02/T02): what each channel costs, by accrual like the revenue of the Home. The gross is the Home's revenue of the very stays of
/// <c>bookings-by-channel</c>; the commission and the net are what the import stored, pro rata per night as the revenue is; a stay whose commission nobody reported is
/// counted apart and never taken as free; the cost of a direct stay is the fees of its card payments, unknown (not zero) while one is not stored; the mode of a channel
/// comes from the portal settings and is unknown when they cannot be read. Numbers worked out by hand, October 2026.
/// </summary>
public class ChannelCostsReportTests
{
    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly EndOfOctober = new(2026, 10, 31);
    private static readonly HostReportQuery OctoberQuery = new(October, EndOfOctober);

    private sealed class FixedModes(Dictionary<BookingSource, ChannelCommissionMode>? modes = null) : IChannelCommissionSettingsProvider
    {
        public Task<IReadOnlyDictionary<BookingSource, ChannelCommissionMode>> GetModesAsync(Guid orgId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<BookingSource, ChannelCommissionMode>>(modes ?? new Dictionary<BookingSource, ChannelCommissionMode>());
    }

    private static ChannelCostsReportService Costs(AppDbContext db, Dictionary<BookingSource, ChannelCommissionMode>? modes = null) => new(db, new FixedModes(modes));

    private static HostDashboardService Home(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    private sealed record World(AppDbContext Db, Guid OrgId, Property Trullo, Property CasaBianca, Guest Guest);

    private static async Task<World> SeedAsync(Action<World>? more = null)
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-nf98", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-nf98", "Casa Bianca");
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

    private static Booking Add(World world, BookingSource source, DateTime checkIn, DateTime checkOut, decimal price, BookingStatus status = BookingStatus.Confirmed, Property? property = null)
    {
        var booking = Stay(property ?? world.Trullo, world.Guest, status, source, checkIn, checkOut, price);
        world.Db.Bookings.Add(booking);
        return booking;
    }

    // A stay of a channel as the import leaves it: its reservation on Octorate and what Octorate gave.
    private static Booking Channel(World world, BookingSource source, DateTime checkIn, DateTime checkOut, decimal price, decimal? commission, decimal? net = null, string? reservation = "RES")
    {
        var booking = Add(world, source, checkIn, checkOut, price);
        booking.OctorateReservationId = reservation;
        booking.OtaCommission = commission;
        booking.OtaNetAmount = net;
        booking.OtaCompanyCollect = true;
        return booking;
    }

    private static ChannelCostRow RowOf(ChannelCostsReport report, BookingSource source) => report.Rows.Single(r => r.Source == source);

    // A cost nobody reported: not available (null), never a zero.
    private static void AssertNotAvailable(ChannelCostRow row)
    {
        Assert.Null(row.Commission);
        Assert.Null(row.Net);
        Assert.Null(row.CostRate);
    }

    // ---------------------------------------------------------------- gross, commission and net

    [Fact]
    public async Task ChannelCosts_CommissionProRataByNights_Test()
    {
        // 29 October to 2 November: four nights, three of them in October and one in November. 400 gross, 60 commission, as the revenue is cut.
        var world = await SeedAsync(w => Channel(w, BookingSource.Airbnb, Day(10, 29), Day(11, 2), 400m, 60m));
        var scope = new HostScope(world.OrgId);

        var october = await Costs(world.Db).GetChannelCostsAsync(scope, OctoberQuery);
        var november = await Costs(world.Db).GetChannelCostsAsync(scope, new HostReportQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));

        var inOctober = RowOf(october, BookingSource.Airbnb);
        Assert.Equal((300m, 45m, 255m, 0.15m, 1, 0), (inOctober.Gross, inOctober.Commission, inOctober.Net, inOctober.CostRate, inOctober.StayCount, inOctober.StaysWithoutCommission));
        var inNovember = RowOf(november, BookingSource.Airbnb);
        Assert.Equal((100m, 15m, 85m, 0.15m), (inNovember.Gross, inNovember.Commission, inNovember.Net, inNovember.CostRate));

        // The two periods cut the stay into its parts: they add up to the whole stay, the way its revenue does.
        Assert.Equal(400m, inOctober.Gross + inNovember.Gross);
        Assert.Equal(60m, inOctober.Commission + inNovember.Commission);

        // The gross is the revenue of the stays of bookings-by-channel for the same days, and the total is the Home's.
        var byChannel = await Home(world.Db).GetBookingsByChannelAsync(scope, OctoberQuery);
        Assert.Equal(byChannel.Rows.Single(r => r.Source == BookingSource.Airbnb).Revenue, inOctober.Gross);
        Assert.Equal(byChannel.Revenue, october.Total.Gross);
        Assert.Equal(ChannelCommissionMode.Unknown, inOctober.CommissionMode);
        Assert.Equal(ChannelCostKind.ChannelCommission, inOctober.Kind);
    }

    [Fact]
    public async Task ChannelCosts_MissingCommission_NotCountedAsZero_Test()
    {
        var world = await SeedAsync(w =>
        {
            // Booking.com: one stay with its commission (20 of 200) and one without (200 of gross): the second is not a free stay.
            Channel(w, BookingSource.BookingCom, Day(10, 5), Day(10, 7), 200m, 20m);
            Channel(w, BookingSource.BookingCom, Day(10, 10), Day(10, 12), 200m, null);
            // Expedia: nothing reported at all. Vrbo: an iCal stay, which has no reservation to report anything.
            Channel(w, BookingSource.Expedia, Day(10, 13), Day(10, 16), 300m, null);
            Channel(w, BookingSource.Vrbo, Day(10, 20), Day(10, 21), 100m, 7m, reservation: null);
            // Airbnb: 5 on 100.
            Channel(w, BookingSource.Airbnb, Day(10, 15), Day(10, 17), 100m, 5m);
        });

        var report = await Costs(world.Db).GetChannelCostsAsync(new HostScope(world.OrgId), OctoberQuery);

        // The rate is the commission over the gross of the stays that reported one (20 / 200), not over the whole channel (20 / 400, which would make a
        // stay without a commission a free one); the net leaves the stay without one out; the stay is counted apart.
        var booking = RowOf(report, BookingSource.BookingCom);
        Assert.Equal((400m, 20m, 180m, 0.1m, 2, 1), (booking.Gross, booking.Commission, booking.Net, booking.CostRate, booking.StayCount, booking.StaysWithoutCommission));

        // A channel where nothing was reported has no cost, no net and no rate: not available, never 0.
        var expedia = RowOf(report, BookingSource.Expedia);
        Assert.Equal(300m, expedia.Gross);
        AssertNotAvailable(expedia);
        Assert.Equal(1, expedia.StaysWithoutCommission);
        var vrbo = RowOf(report, BookingSource.Vrbo);
        Assert.Equal(100m, vrbo.Gross);
        AssertNotAvailable(vrbo);
        Assert.Equal(1, vrbo.StaysWithoutCommission);

        // The rows are ordered by rate, the highest first, the ones without a rate last; only the first is the most expensive.
        Assert.Equal(
            new[] { BookingSource.BookingCom, BookingSource.Airbnb, BookingSource.Expedia, BookingSource.Vrbo },
            report.Rows.Select(r => r.Source!.Value));
        Assert.Equal(new[] { BookingSource.BookingCom }, report.Rows.Where(r => r.MostExpensive).Select(r => r.Source!.Value));

        // The total is worked out over all the stays: the Home's gross, the costs that are known over the gross they belong to.
        Assert.Equal((900m, 25m, 275m, 0.0833m, 5, 3), (report.Total.Gross, report.Total.Commission, report.Total.Net, report.Total.CostRate, report.Total.StayCount, report.Total.StaysWithoutCommission));
        Assert.Null(report.Total.Source);
        Assert.False(report.Total.MostExpensive);
    }

    [Fact]
    public async Task ChannelCosts_TheNetTheChannelReported_IsTheNetOfTheStay()
    {
        var world = await SeedAsync(w =>
        {
            // The channel says 330 reached the host, whatever gross minus commission would give (340); its stay counts for it.
            Channel(w, BookingSource.Airbnb, Day(10, 1), Day(10, 5), 400m, 60m, 330m);
            // A net without a commission: the net is known and the commission is not (the stay is counted apart, and the rate has nothing to stand on).
            Channel(w, BookingSource.BookingCom, Day(10, 20), Day(10, 22), 200m, null, 150m);
        });

        var report = await Costs(world.Db).GetChannelCostsAsync(new HostScope(world.OrgId), OctoberQuery);

        var airbnb = RowOf(report, BookingSource.Airbnb);
        Assert.Equal((400m, 60m, 330m, 0.15m), (airbnb.Gross, airbnb.Commission, airbnb.Net, airbnb.CostRate));
        var booking = RowOf(report, BookingSource.BookingCom);
        Assert.Equal((200m, 150m, 1), (booking.Gross, booking.Net, booking.StaysWithoutCommission));
        Assert.Null(booking.Commission);
        Assert.Null(booking.CostRate);
    }

    // ---------------------------------------------------------------- the direct stays and the stays no one charges

    [Fact]
    public async Task ChannelCosts_TheDirectStays_CostTheFeesOfTheirCardPayments_NeverZeroWhileOneIsNotStored()
    {
        var world = await SeedAsync(w =>
        {
            Booking Direct(decimal price, DateTime checkIn, BookingStatus status = BookingStatus.Confirmed) => Add(w, BookingSource.Direct, checkIn, checkIn.AddDays(2), price, status);

            var paidByCard = Direct(300m, Day(10, 1));
            w.Db.Payments.AddRange(
                CardPay(paidByCard, 200m, At(9, 20, 9), 4.75m, 195.25m, At(9, 22, 0)),
                // A cash payment has no fee: it is no card payment and is not waiting for one.
                Pay(paidByCard, 100m, PaymentStatus.Completed, PaymentMethod.CashOnArrival, At(9, 20, 9)));
            var twoCards = Direct(200m, Day(10, 4));
            w.Db.Payments.AddRange(
                CardPay(twoCards, 100m, At(9, 21, 9), 3.00m, 97m, At(9, 23, 0)),
                CardPay(twoCards, 100m, At(9, 22, 9), 2.50m, 97.5m, At(9, 24, 0)));
            // The fee of the payment is not stored (yet): the cost of the stay is not known.
            w.Db.Payments.Add(CardPay(Direct(100m, Day(10, 7)), 100m, At(9, 22, 9), null, null, null));
            // One of two card payments has no fee yet: half a cost is not a cost.
            var halfKnown = Direct(100m, Day(10, 10));
            w.Db.Payments.AddRange(
                CardPay(halfKnown, 50m, At(9, 22, 9), 1.00m, 49m, At(9, 24, 0)),
                CardPay(halfKnown, 50m, At(9, 23, 9), null, null, null));
            // Paid in cash alone: no card payment, no fee, no cost.
            var cash = Direct(100m, Day(10, 13));
            w.Db.Payments.Add(Pay(cash, 100m, PaymentStatus.Completed, PaymentMethod.CashOnArrival, At(10, 13, 9)));
            // A fee in francs cannot be added to a stay in euros.
            w.Db.Payments.Add(CardPay(Direct(100m, Day(10, 16)), 100m, At(9, 22, 9), 2.00m, 98m, At(9, 24, 0), "CHF"));
            // A card payment that failed never settled: it has no fee and costs nothing.
            w.Db.Payments.Add(CardPay(Direct(100m, Day(10, 19)), 100m, At(9, 22, 9), null, null, null, status: PaymentStatus.Failed));
            // Not a stay: a cancelled booking.
            Direct(900m, Day(10, 22), BookingStatus.Cancelled);
        });

        var report = await Costs(world.Db).GetChannelCostsAsync(new HostScope(world.OrgId), OctoberQuery);

        // Seven stays (1 000 gross); four have a known cost: 4.75 + 5.50 + 0 + 0 = 10.25 on the 700 of gross that is theirs; three do not and are counted apart.
        var direct = RowOf(report, BookingSource.Direct);
        Assert.Equal(
            (7, 1000m, 10.25m, 689.75m, 0.0146m, 3, ChannelCostKind.PaymentFee),
            (direct.StayCount, direct.Gross, direct.Commission, direct.Net, direct.CostRate, direct.StaysWithoutCommission, direct.Kind));
        Assert.Null(direct.CommissionMode);
    }

    [Fact]
    public async Task ChannelCosts_TheDirectStaysWithoutAnyStoredFee_AreNotAvailable_NotAZeroFee()
    {
        var world = await SeedAsync(w =>
        {
            var stay = Add(w, BookingSource.Direct, Day(10, 1), Day(10, 3), 300m);
            w.Db.Payments.Add(CardPay(stay, 300m, At(9, 20, 9), null, null, null));
        });

        var report = await Costs(world.Db).GetChannelCostsAsync(new HostScope(world.OrgId), OctoberQuery);

        var direct = RowOf(report, BookingSource.Direct);
        Assert.Equal(300m, direct.Gross);
        AssertNotAvailable(direct);
        Assert.Equal(1, direct.StaysWithoutCommission);
        Assert.False(direct.MostExpensive);
    }

    [Fact]
    public async Task ChannelCosts_TheStaysTheHostEnteredAndLocalOnes_HaveNoCost_AndTheBookingEngineCostsItsReportedCommission()
    {
        var world = await SeedAsync(w =>
        {
            Add(w, BookingSource.Manual, Day(10, 1), Day(10, 3), 500m);
            Add(w, BookingSource.Local, Day(10, 5), Day(10, 7), 300m);
            // A reservation of the booking engine of the agency (Direct, a reservation, no row of the mirror): its money is the channel manager's, so the commission it
            // reported is the cost; a Direct stay CasaZen sent to Octorate (a row of the mirror) is CasaZen's and costs the fees of its card payments.
            Channel(w, BookingSource.Direct, Day(10, 10), Day(10, 12), 300m, 30m, 270m, "RES-ENGINE");
            var pushed = Channel(w, BookingSource.Direct, Day(10, 14), Day(10, 16), 200m, null, null, "RES-PUSHED");
            w.Db.OctorateBookingSyncs.Add(new OctorateBookingSync { OrgId = w.OrgId, BookingId = pushed.Id, PropertyId = w.Trullo.Id, State = OctorateSyncState.Synced });
            w.Db.Payments.Add(CardPay(pushed, 200m, At(10, 1, 9), 3.25m, 196.75m, At(10, 3, 0)));
        });

        var report = await Costs(world.Db).GetChannelCostsAsync(new HostScope(world.OrgId), OctoberQuery);

        var manual = RowOf(report, BookingSource.Manual);
        Assert.Equal((500m, 0m, 500m, 0m, 0, ChannelCostKind.NoCost), (manual.Gross, manual.Commission, manual.Net, manual.CostRate, manual.StaysWithoutCommission, manual.Kind));
        var local = RowOf(report, BookingSource.Local);
        Assert.Equal((300m, 0m, 300m, 0m), (local.Gross, local.Commission, local.Net, local.CostRate));
        var direct = RowOf(report, BookingSource.Direct);
        Assert.Equal((500m, 33.25m, 466.75m, 0, 2), (direct.Gross, direct.Commission, direct.Net, direct.StaysWithoutCommission, direct.StayCount));
        // Nobody is the most expensive at zero, and the direct row (6,65 %) is the dearest here.
        Assert.Equal(new[] { BookingSource.Direct }, report.Rows.Where(r => r.MostExpensive).Select(r => r.Source!.Value));
    }

    // ---------------------------------------------------------------- the modes, the stays, the reach

    [Fact]
    public async Task ChannelCosts_TheModeOfAChannelIsFromThePortalSettings_AndUnknownWhenTheyCannotBeRead()
    {
        var world = await SeedAsync(w =>
        {
            Channel(w, BookingSource.Airbnb, Day(10, 1), Day(10, 3), 200m, 20m);
            Channel(w, BookingSource.BookingCom, Day(10, 5), Day(10, 7), 200m, 30m);
            Channel(w, BookingSource.Expedia, Day(10, 9), Day(10, 11), 200m, 10m);
            Add(w, BookingSource.Direct, Day(10, 13), Day(10, 15), 200m);
        });
        var scope = new HostScope(world.OrgId);
        var settings = new Dictionary<BookingSource, ChannelCommissionMode>
        {
            [BookingSource.Airbnb] = ChannelCommissionMode.DeductedFromPayout,
            [BookingSource.BookingCom] = ChannelCommissionMode.InvoicedSeparately,
        };

        var read = await Costs(world.Db, settings).GetChannelCostsAsync(scope, OctoberQuery);
        var notRead = await Costs(world.Db).GetChannelCostsAsync(scope, OctoberQuery);

        Assert.Equal(
            new (BookingSource, ChannelCommissionMode?)[]
            {
                (BookingSource.BookingCom, ChannelCommissionMode.InvoicedSeparately),
                (BookingSource.Airbnb, ChannelCommissionMode.DeductedFromPayout),
                (BookingSource.Expedia, ChannelCommissionMode.Unknown),
                (BookingSource.Direct, null),
            },
            read.Rows.Select(r => (r.Source!.Value, r.CommissionMode)).ToArray());
        // The report still works from the stored amounts: only the modes are unknown.
        Assert.All(notRead.Rows.Where(r => r.Kind == ChannelCostKind.ChannelCommission), r => Assert.Equal(ChannelCommissionMode.Unknown, r.CommissionMode));
        Assert.Equal(read.Rows.Select(r => (r.Source, r.Gross, r.Commission)), notRead.Rows.Select(r => (r.Source, r.Gross, r.Commission)));
    }

    [Fact]
    public async Task ChannelCosts_TheStaysAreExactlyThoseOfBookingsByChannel_AndTheTotalIsTheHomesRevenue()
    {
        var world = await SeedAsync(w =>
        {
            Channel(w, BookingSource.Airbnb, Day(10, 2), Day(10, 5), 300m, 36m);
            Channel(w, BookingSource.Airbnb, Day(10, 30), Day(11, 3), 400m, 48m);
            Add(w, BookingSource.Direct, Day(10, 8), Day(10, 10), 250m, BookingStatus.CheckedIn);
            Add(w, BookingSource.Manual, Day(10, 12), Day(10, 14), 100m, BookingStatus.CheckedOut, w.CasaBianca);
            // Not stays of the revenue: a cancelled booking, and a request still waiting for the host.
            Add(w, BookingSource.Airbnb, Day(10, 15), Day(10, 18), 900m, BookingStatus.Cancelled);
            var request = Add(w, BookingSource.Direct, Day(10, 20), Day(10, 22), 800m, BookingStatus.Pending);
            request.PaymentOption = PaymentOption.OnSite;
            // Outside the days of the report.
            Add(w, BookingSource.Airbnb, Day(9, 20), Day(9, 25), 700m);
        });
        var scope = new HostScope(world.OrgId);

        var costs = await Costs(world.Db).GetChannelCostsAsync(scope, OctoberQuery);
        var byChannel = await Home(world.Db).GetBookingsByChannelAsync(scope, OctoberQuery);
        var home = await Home(world.Db).GetKpisAsync(scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, October));

        Assert.Equal(
            byChannel.Rows.OrderBy(r => r.Source).Select(r => (r.Source, r.BookingCount, r.Revenue)),
            costs.Rows.OrderBy(r => r.Source).Select(r => (r.Source!.Value, r.StayCount, r.Gross)));
        Assert.Equal(home.Revenue, costs.Total.Gross);
        Assert.Equal(home.RevenueStayCount, costs.Total.StayCount);
    }

    [Fact]
    public async Task ChannelCosts_ShowOnlyWhatTheScopeReaches_AndAPropertyOutsideItIsAnother404()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);

        var restricted = await Costs(db).GetChannelCostsAsync(world.Restricted, OctoberQuery);
        var orgWide = await Costs(db).GetChannelCostsAsync(world.OrgWide, OctoberQuery);

        // Each property has two stays of 300 in October (the world's): the collaborator sees the granted property's, the org both, nobody the other org's.
        Assert.True(restricted.Total.Gross > 0m);
        Assert.Equal(orgWide.Total.Gross, restricted.Total.Gross * 2);
        Assert.Equal(orgWide.Total.StayCount, restricted.Total.StayCount * 2);
        foreach (var propertyId in new[] { world.Hidden.Id, world.OtherOrgProperty.Id, Guid.NewGuid() })
        {
            var thrown = await Assert.ThrowsAsync<NotFoundException>(
                () => Costs(db).GetChannelCostsAsync(world.Restricted, new HostReportQuery(October, EndOfOctober, propertyId)));
            Assert.Equal("PropertyNotFound", thrown.MessageKey);
        }

        await Assert.ThrowsAsync<NotFoundException>(
            () => Costs(db).GetChannelCostsAsync(world.OrgWide, new HostReportQuery(October, EndOfOctober, world.OtherOrgProperty.Id)));
        var one = await Costs(db).GetChannelCostsAsync(world.OrgWide, new HostReportQuery(October, EndOfOctober, world.Hidden.Id));
        Assert.Equal(restricted.Total.Gross, one.Total.Gross);
    }

    // ---------------------------------------------------------------- the settings of the portals

    private sealed class Settings
    {
        public AppDbContext Db { get; } = HostScopeScenario.NewInMemoryDb();

        public Guid OrgId { get; } = Guid.NewGuid();

        public ChannelCommissionSettingsProvider Provider(string environment = "Staging")
        {
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns(environment);
            return new ChannelCommissionSettingsProvider(Db, new BookingStateMapper(), host.Object);
        }

        public async Task ConnectAsync(OctorateEnvironment environment = OctorateEnvironment.Test, OctorateConnectionStatus status = OctorateConnectionStatus.Connected, Guid? orgId = null)
        {
            Db.OctorateConnections.Add(new OctorateConnection { OrgId = orgId ?? OrgId, Environment = environment, Status = status });
            await Db.SaveChangesAsync();
        }

        public async Task PortalAsync(string code, string name, bool included, bool? invoiced, bool listed = true)
        {
            Db.MarketplacePortals.Add(new MarketplacePortal
            {
                PortalCode = code,
                Name = name,
                CommissionIncludedInPayments = included,
                CommissionsInvoiced = invoiced,
                IsListed = listed,
                Enabled = true,
            });
            await Db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Settings_TheModeOfEachChannelIsFromItsPortal_MappedLikeTheImportMapsAReservation()
    {
        var settings = new Settings();
        await settings.ConnectAsync();
        // The ids of the sandbox (the portal code is the channel id), and a name for a portal the id does not tell.
        await settings.PortalAsync("274", "Airbnb", included: true, invoiced: null);
        await settings.PortalAsync("235", "Vrbo", included: true, invoiced: false);
        await settings.PortalAsync("142", "Booking.com", included: false, invoiced: true);
        await settings.PortalAsync("4", "Expedia", included: false, invoiced: null);
        await settings.PortalAsync("900", "Agoda Partners", included: false, invoiced: true);

        var modes = await settings.Provider().GetModesAsync(settings.OrgId);

        Assert.Equal(ChannelCommissionMode.DeductedFromPayout, modes[BookingSource.Airbnb]);
        Assert.Equal(ChannelCommissionMode.DeductedFromPayout, modes[BookingSource.Vrbo]);
        Assert.Equal(ChannelCommissionMode.InvoicedSeparately, modes[BookingSource.BookingCom]);
        // Neither flag says it (an absent flag is not a no), and a portal known by its name only maps like a reservation of it does.
        Assert.Equal(ChannelCommissionMode.Unknown, modes[BookingSource.Expedia]);
        Assert.Equal(ChannelCommissionMode.InvoicedSeparately, modes[BookingSource.Agoda]);
    }

    [Fact]
    public async Task Settings_WhatItDoesNotClaim_IsLeftOut_OrUnknown()
    {
        var settings = new Settings();
        await settings.ConnectAsync();
        await settings.PortalAsync("274", "Airbnb", included: true, invoiced: null);
        // Two portals of one channel that disagree: the channel is unknown, not the first one's.
        await settings.PortalAsync("142", "Booking.com", included: false, invoiced: true);
        await settings.PortalAsync("9142", "Booking.com Business", included: true, invoiced: null);
        // A portal that maps to no channel of its own (many do), the booking engine of the agency (a direct channel) and a portal the catalog no longer lists.
        await settings.PortalAsync("555", "Holidu", included: false, invoiced: true);
        await settings.PortalAsync("270", "Octorate booking engine", included: true, invoiced: null);
        await settings.PortalAsync("4", "Expedia", included: true, invoiced: null, listed: false);

        var modes = await settings.Provider().GetModesAsync(settings.OrgId);

        Assert.Equal(ChannelCommissionMode.DeductedFromPayout, modes[BookingSource.Airbnb]);
        Assert.Equal(ChannelCommissionMode.Unknown, modes[BookingSource.BookingCom]);
        Assert.DoesNotContain(BookingSource.OtherChannel, modes.Keys);
        Assert.DoesNotContain(BookingSource.Direct, modes.Keys);
        Assert.DoesNotContain(BookingSource.Expedia, modes.Keys);
        Assert.Equal(2, modes.Count);
    }

    [Theory]
    [InlineData("none", "Staging")]
    [InlineData("disconnected", "Staging")]
    [InlineData("other-environment", "Production")]
    [InlineData("other-org", "Staging")]
    public async Task Settings_AnOrgThatIsNotConnectedToOctorateHere_HasNone_SoTheReportWorksFromTheStoredAmounts(string scenario, string environment)
    {
        var settings = new Settings();
        switch (scenario)
        {
            case "disconnected":
                await settings.ConnectAsync(status: OctorateConnectionStatus.Disconnected);
                break;
            case "other-environment":
                // Connected to the sandbox, asked in production: another environment is another account.
                await settings.ConnectAsync(OctorateEnvironment.Test);
                break;
            case "other-org":
                await settings.ConnectAsync(orgId: Guid.NewGuid());
                break;
        }

        await settings.PortalAsync("274", "Airbnb", included: true, invoiced: null);

        var modes = await settings.Provider(environment).GetModesAsync(settings.OrgId);

        Assert.Empty(modes);
    }
}
