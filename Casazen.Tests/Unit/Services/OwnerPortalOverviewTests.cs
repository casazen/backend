using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.OwnerPortal;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class OwnerPortalOverviewTests
{
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly OctoberEnd = new(2026, 10, 31);

    private const string GuestName = "Mario";
    private const string GuestSurname = "Ospitale";
    private const string GuestEmail = "mario.ospitale@example.com";
    private const string GuestPhone = "+39 099 8877665";

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Booking Stay(Property house, Guid guestId, DateTime checkIn, DateTime checkOut, decimal basePrice) => new()
    {
        OrgId = house.OrgId,
        PropertyId = house.Id,
        GuestId = guestId,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        NumberOfGuests = 2,
        BasePrice = basePrice,
        TotalPrice = basePrice,
    };

    private static Property NewHouse(Guid orgId, string name)
    {
        var house = HostScopeScenario.NewProperty(orgId, "auth0|agenzia-nf96", name);
        house.CreatedAt = Day(2024, 1, 1);
        return house;
    }

    private static OwnerUser NewOwnerUser(string first) => new()
    {
        Auth0Sub = $"auth0|{first.ToLowerInvariant()}-{Guid.NewGuid():N}",
        Email = $"{first.ToLowerInvariant()}.{Guid.NewGuid():N}@example.com",
        FirstName = first,
        LastName = "Prova",
        CreatedAt = Day(2024, 1, 1),
    };

    private static PropertyOwnership Owns(
        OwnerUser owner, PropertyOwner registry, Property house, decimal share, DateTime start, DateTime? end = null)
    {
        return new PropertyOwnership
        {
            OrgId = house.OrgId,
            PropertyId = house.Id,
            PropertyOwnerId = registry.Id,
            OwnerUserId = owner.Id,
            SharePercent = share,
            OwnershipStart = start,
            OwnershipEnd = end,
        };
    }

    private sealed record World(
        HostDashboardService Dashboard,
        OwnerPortalService Service,
        Guid OrgId,
        Guid OtherOrgId,
        OwnerUser Alice,
        OwnerUser Bob,
        OwnerUser Carol,
        Property Villa,
        Property Sola,
        Property Altrui,
        Property Venduta,
        Property VillaDue);

    private static async Task<World> SeedAsync(Action<AppDbContext, World>? more = null)
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var villa = NewHouse(orgId, "Villa");
        var sola = NewHouse(orgId, "Casa Sola");
        var altrui = NewHouse(orgId, "Casa Altrui");
        var venduta = NewHouse(orgId, "Casa Venduta");
        var villaDue = NewHouse(otherOrgId, "Villa Due");
        var alice = NewOwnerUser("Alice");
        var bob = NewOwnerUser("Bob");
        var carol = NewOwnerUser("Carol");
        var registry = new PropertyOwner { OrgId = orgId, FirstName = "Registro", LastName = "Uno", Email = $"{Guid.NewGuid():N}@example.com" };
        var registryDue = new PropertyOwner { OrgId = otherOrgId, FirstName = "Registro", LastName = "Due", Email = $"{Guid.NewGuid():N}@example.com" };
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = GuestName,
            LastName = GuestSurname,
            Email = GuestEmail,
            PhoneNumber = GuestPhone,
        };
        var guestDue = new Guest { OrgId = otherOrgId, FirstName = "Ospite", LastName = "Due", Email = $"{Guid.NewGuid():N}@example.com" };

        db.Properties.AddRange(villa, sola, altrui, venduta, villaDue);
        db.OwnerUsers.AddRange(alice, bob, carol);
        db.PropertyOwners.AddRange(registry, registryDue);
        db.Guests.AddRange(guest, guestDue);
        db.Bookings.AddRange(
            Stay(villa, guest.Id, Day(2026, 10, 10), Day(2026, 10, 14), 400m),
            Stay(villa, guest.Id, Day(2025, 10, 14), Day(2025, 10, 17), 300m),
            Stay(sola, guest.Id, Day(2026, 10, 20), Day(2026, 10, 22), 200m),
            Stay(sola, guest.Id, Day(2026, 9, 20), Day(2026, 9, 30), 150m),
            Stay(altrui, guest.Id, Day(2026, 10, 5), Day(2026, 10, 10), 500m),
            Stay(venduta, guest.Id, Day(2026, 10, 1), Day(2026, 10, 8), 700m),
            Stay(villaDue, guestDue.Id, Day(2026, 10, 3), Day(2026, 10, 6), 350m));
        db.PropertyOwnerships.AddRange(
            Owns(alice, registry, villa, 50m, Day(2024, 1, 1)),
            Owns(bob, registry, villa, 50m, Day(2024, 1, 1)),
            Owns(alice, registry, sola, 100m, Day(2024, 1, 1)),
            Owns(carol, registry, altrui, 100m, Day(2024, 1, 1)),
            Owns(alice, registry, venduta, 100m, Day(2024, 1, 1), Day(2026, 6, 30)),
            Owns(alice, registryDue, villaDue, 100m, Day(2024, 1, 1)));

        var dashboard = new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock);
        var world = new World(
            dashboard, new OwnerPortalService(db, Clock, dashboard), orgId, otherOrgId, alice, bob, carol, villa, sola, altrui, venduta, villaDue);
        more?.Invoke(db, world);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return world;
    }

    private static OwnerOverviewQuery Query(HostPerformanceGroupBy groupBy = HostPerformanceGroupBy.Property, bool compare = false) =>
        new(October, OctoberEnd, groupBy, compare);

    [Fact]
    public async Task OwnerPortalOverview_ShareWeightedRevenue_Test()
    {
        var world = await SeedAsync();
        var home = await world.Dashboard.GetKpisAsync(
            new HostScope(world.OrgId), new HostDashboardQuery(HostDashboardPeriodKind.Month, October, world.Villa.Id));

        var alice = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query(compare: true));
        var bob = await world.Service.GetOwnerOverviewAsync(world.Bob.Id, Query(compare: true));

        Assert.Equal(400m, home.Revenue);
        var aliceVilla = alice.Rows.Single(r => r.PropertyId == world.Villa.Id).Figures;
        var bobVilla = bob.Rows.Single(r => r.PropertyId == world.Villa.Id).Figures;
        Assert.Equal(200m, aliceVilla.Revenue);
        Assert.Equal(200m, bobVilla.Revenue);
        Assert.Equal(home.Revenue, aliceVilla.Revenue + bobVilla.Revenue);
        Assert.Equal(home.Occupancy.OccupiedNights, aliceVilla.OccupiedNights + bobVilla.OccupiedNights);
        Assert.Equal(home.Occupancy.AvailableNights, aliceVilla.AvailableNights + bobVilla.AvailableNights);

        Assert.Equal(new[] { "Casa Sola", "Villa", "Villa Due" }, alice.Rows.Select(r => r.Label).ToArray());
        Assert.Equal(750m, alice.Total.Figures.Revenue);
        Assert.Equal(7m, alice.Total.Figures.OccupiedNights);
        Assert.Equal(77.5m, alice.Total.Figures.AvailableNights);
        Assert.Equal(107.14m, alice.Total.Figures.Adr);
        Assert.Equal(9.68m, alice.Total.Figures.RevPar);
        Assert.Equal(200m, bob.Total.Figures.Revenue);
        Assert.Equal(100m, bob.Total.Figures.Adr);

        Assert.Equal(150m, alice.Total.PreviousYear!.Revenue);
        Assert.Equal(150m, bob.Total.PreviousYear!.Revenue);
        Assert.Equal(77.5m, alice.Total.PreviousYear.AvailableNights);
        Assert.Equal(Day(2025, 10, 1), alice.PreviousYearPeriod!.From);

        var dto = OwnerPortalOverviewDto.From(alice, HostPerformanceGroupBy.Property, "Prestazioni per immobile, per competenza");
        Assert.Equal(400.0m, dto.Total.RevenueDeltaPercent);
        Assert.Equal(150m, dto.Total.PreviousYear!.Revenue);
        Assert.Equal(15_000L, dto.Total.PreviousYear.RevenueCents);
    }

    [Fact]
    public async Task OwnerPortalOverview_OtherOwnersProperties_NotCounted_Test()
    {
        var world = await SeedAsync();

        var alice = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query());

        Assert.DoesNotContain(alice.Rows, r => r.PropertyId == world.Altrui.Id || r.PropertyId == world.Venduta.Id);
        Assert.Equal(750m, alice.Total.Figures.Revenue);

        foreach (var notHers in new[] { world.Altrui.Id, world.Venduta.Id, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<NotFoundException>(() =>
                world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query() with { PropertyId = notHers }));
            await Assert.ThrowsAsync<NotFoundException>(() =>
                world.Service.GetOwnerTrendAsync(world.Alice.Id, Query() with { PropertyId = notHers }));
        }

        var sola = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query() with { PropertyId = world.Sola.Id });
        Assert.Equal(world.Sola.Id, Assert.Single(sola.Rows).PropertyId);
        Assert.Equal(200m, sola.Total.Figures.Revenue);
        var carol = await world.Service.GetOwnerOverviewAsync(world.Carol.Id, Query());
        Assert.Equal(world.Altrui.Id, Assert.Single(carol.Rows).PropertyId);
        Assert.Equal(500m, carol.Total.Figures.Revenue);

        var nobody = await world.Service.GetOwnerOverviewAsync(Guid.NewGuid(), Query(compare: true));
        Assert.Empty(nobody.Rows);
        Assert.Equal(0m, nobody.Total.Figures.Revenue);
        Assert.Null(nobody.Total.PreviousYear);
        var nobodyTrend = await world.Service.GetOwnerTrendAsync(Guid.NewGuid(), Query(HostPerformanceGroupBy.None));
        Assert.Equal(0m, Assert.Single(nobodyTrend.Series).Points.Sum(p => p.Revenue));
    }

    [Fact]
    public async Task OwnerPortalOverview_AnOwnershipThatStartedAfterTheDays_IsNotInThem()
    {
        Guid appena = default;
        var world = await SeedAsync((db, w) =>
        {
            var house = NewHouse(w.OrgId, "Casa Appena Comprata");
            appena = house.Id;
            var guest = db.Guests.Local.First(g => g.OrgId == w.OrgId);
            var registry = db.PropertyOwners.Local.First(o => o.OrgId == w.OrgId);
            db.Properties.Add(house);
            db.Bookings.Add(Stay(house, guest.Id, Day(2026, 10, 6), Day(2026, 10, 8), 160m));
            db.PropertyOwnerships.Add(Owns(w.Alice, registry, house, 100m, Day(2026, 10, 5)));
        });

        var september = await world.Service.GetOwnerOverviewAsync(
            world.Alice.Id, new OwnerOverviewQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), HostPerformanceGroupBy.Property));
        var october = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query());

        Assert.DoesNotContain(september.Rows, r => r.PropertyId == appena);
        Assert.Contains(october.Rows, r => r.PropertyId == appena);
        await Assert.ThrowsAsync<NotFoundException>(() => world.Service.GetOwnerOverviewAsync(
            world.Alice.Id,
            new OwnerOverviewQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)) { PropertyId = appena }));
    }

    [Fact]
    public async Task OwnerPortalOverview_NoGuestData_Test()
    {
        var world = await SeedAsync();

        var overview = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query(compare: true));
        var trend = await world.Service.GetOwnerTrendAsync(world.Alice.Id, Query(compare: true));
        var overviewJson = JsonSerializer.Serialize(OwnerPortalOverviewDto.From(overview, HostPerformanceGroupBy.Property, "Prestazioni"));
        var trendJson = JsonSerializer.Serialize(OwnerPortalTrendDto.From(trend, HostPerformanceGroupBy.Property, "Andamento"));

        foreach (var json in new[] { overviewJson, trendJson })
        {
            foreach (var secret in new[] { GuestName, GuestSurname, GuestEmail, GuestPhone, world.Bob.Email, world.Carol.Email, world.Bob.Auth0Sub })
                Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("Altrui", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Venduta", json, StringComparison.Ordinal);
        }

        Assert.Contains("Casa Sola", overviewJson, StringComparison.Ordinal);

        foreach (var type in new[]
                 {
                     typeof(OwnerPortalOverview), typeof(OwnerPortalRow), typeof(OwnerPortalTrend), typeof(OwnerPortalSeries),
                     typeof(OwnerPortalOverviewDto), typeof(OwnerPortalRowDto), typeof(OwnerPortalTrendDto), typeof(OwnerPortalSeriesDto),
                     typeof(WeightedFiguresDto),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Guest", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("Email", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("Phone", StringComparison.OrdinalIgnoreCase)
                                              || n.Contains("Document", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task OwnerPortalTrend_WeightedBucketsAddUpToTheOverview_WithTheYearBefore()
    {
        var world = await SeedAsync();
        var overview = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query(HostPerformanceGroupBy.None, compare: true));

        var together = await world.Service.GetOwnerTrendAsync(world.Alice.Id, Query(HostPerformanceGroupBy.None, compare: true));

        Assert.Equal(HostOverviewSeriesUnit.Week, together.Unit);
        Assert.Equal(5, together.Buckets.Count);
        var line = Assert.Single(together.Series);
        Assert.Equal(HostPerformanceRules.TotalKey, line.Key);
        Assert.Equal(overview.Total.Figures.Revenue, Math.Round(line.Points.Sum(p => p.Revenue), 2));
        Assert.Equal(overview.Total.Figures.OccupiedNights, line.Points.Sum(p => p.OccupiedNights));
        Assert.Equal(overview.Total.Figures.AvailableNights, line.Points.Sum(p => p.AvailableNights));
        Assert.Equal(5, line.PreviousYear.Count);
        Assert.Equal(150m, Math.Round(line.PreviousYear.Sum(p => p!.Revenue), 2));
        Assert.Equal(150m, line.PreviousYear[2]!.Revenue);

        var byProperty = await world.Service.GetOwnerTrendAsync(world.Alice.Id, Query());
        Assert.Equal(new[] { "Villa Due", "Casa Sola", "Villa" }, byProperty.Series.Select(s => s.Label).ToArray());
        var rows = (await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query())).Rows;
        Assert.All(byProperty.Series, series =>
            Assert.Equal(rows.Single(r => r.PropertyId == series.PropertyId).Figures.Revenue, Math.Round(series.Points.Sum(p => p.Revenue), 2)));
    }

    [Fact]
    public async Task OwnerPortalProperties_TheOccupancyIsTheHomes_NotThePlaceholderOfZero()
    {
        var world = await SeedAsync();

        var properties = await world.Service.GetOwnerPropertiesAsync(world.Alice.Id);

        Assert.Equal(33.3, properties.Single(p => p.PropertyId == world.Sola.Id).OccupancyRate);
        Assert.Equal(10.0, properties.Single(p => p.PropertyId == world.VillaDue.Id).OccupancyRate);
        Assert.Equal(0.0, properties.Single(p => p.PropertyId == world.Villa.Id).OccupancyRate);
        Assert.DoesNotContain(properties, p => p.PropertyId == world.Venduta.Id);

        var withoutHome = new OwnerPortalService(HostScopeScenario.NewInMemoryDb(), Clock);
        Assert.Empty(await withoutHome.GetOwnerPropertiesAsync(world.Alice.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutHome.GetOwnerOverviewAsync(world.Alice.Id, Query()));
    }
}
