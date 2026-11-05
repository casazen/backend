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

/// <summary>
/// NF-96: the owner portal shows the owner's properties on the Home's numbers, weighted by the share the owner holds. October 2026, worked
/// out by hand. Org one: Villa (Alice 50%, Bob 50%) with a 400 stay of 4 nights, Casa Sola (Alice) with a 200 one of 2, Casa Altrui (Carol)
/// with a 500 one of 5, Casa Venduta (Alice until 30 June) with a 700 one of 7. Org two: Villa Due (Alice) with a 350 stay of 3 nights.
/// A year earlier the Villa had a 300 stay of 3 nights.
/// </summary>
public class OwnerPortalOverviewTests
{
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly OctoberEnd = new(2026, 10, 31);

    // What a guest is: nothing of this may be in an answer of the portal.
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

    // ---------------------------------------------------------------- proof: the revenue is weighted by the share

    [Fact]
    public async Task OwnerPortalOverview_ShareWeightedRevenue_Test()
    {
        var world = await SeedAsync();
        var home = await world.Dashboard.GetKpisAsync(
            new HostScope(world.OrgId), new HostDashboardQuery(HostDashboardPeriodKind.Month, October, world.Villa.Id));

        var alice = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query(compare: true));
        var bob = await world.Service.GetOwnerOverviewAsync(world.Bob.Id, Query(compare: true));

        // The villa is shared 50/50: half of its 400 for each owner, and the two halves are the Home's revenue for the villa.
        Assert.Equal(400m, home.Revenue);
        var aliceVilla = alice.Rows.Single(r => r.PropertyId == world.Villa.Id).Figures;
        var bobVilla = bob.Rows.Single(r => r.PropertyId == world.Villa.Id).Figures;
        Assert.Equal(200m, aliceVilla.Revenue);
        Assert.Equal(200m, bobVilla.Revenue);
        Assert.Equal(home.Revenue, aliceVilla.Revenue + bobVilla.Revenue);
        Assert.Equal(home.Occupancy.OccupiedNights, aliceVilla.OccupiedNights + bobVilla.OccupiedNights);
        Assert.Equal(home.Occupancy.AvailableNights, aliceVilla.AvailableNights + bobVilla.AvailableNights);

        // Alice: half of the villa, all of Casa Sola and all of Villa Due (another org): 200 + 200 + 350. Her rows are by name.
        Assert.Equal(new[] { "Casa Sola", "Villa", "Villa Due" }, alice.Rows.Select(r => r.Label).ToArray());
        Assert.Equal(750m, alice.Total.Figures.Revenue);
        Assert.Equal(7m, alice.Total.Figures.OccupiedNights);   // 2 + 2 + 3
        Assert.Equal(77.5m, alice.Total.Figures.AvailableNights);   // 15.5 + 31 + 31
        Assert.Equal(107.14m, alice.Total.Figures.Adr);   // 750 over 7 nights
        Assert.Equal(9.68m, alice.Total.Figures.RevPar);   // 750 over 77.5 nights, 9.677...
        Assert.Equal(200m, bob.Total.Figures.Revenue);
        Assert.Equal(100m, bob.Total.Figures.Adr);

        // The year before: the villa had 300, half of it each; the other houses existed and had nothing (zeros that are real).
        Assert.Equal(150m, alice.Total.PreviousYear!.Revenue);
        Assert.Equal(150m, bob.Total.PreviousYear!.Revenue);
        Assert.Equal(77.5m, alice.Total.PreviousYear.AvailableNights);
        Assert.Equal(Day(2025, 10, 1), alice.PreviousYearPeriod!.From);

        // And what changed is the server's: 750 against 150.
        var dto = OwnerPortalOverviewDto.From(alice, HostPerformanceGroupBy.Property, "Prestazioni per immobile, per competenza");
        Assert.Equal(400.0m, dto.Total.RevenueDeltaPercent);
        Assert.Equal(150m, dto.Total.PreviousYear!.Revenue);
        Assert.Equal(15_000L, dto.Total.PreviousYear.RevenueCents);
    }

    // ---------------------------------------------------------------- proof: an owner never reads a property it does not own

    [Fact]
    public async Task OwnerPortalOverview_OtherOwnersProperties_NotCounted_Test()
    {
        var world = await SeedAsync();

        var alice = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query());

        // Casa Altrui is Carol's and Casa Venduta was Alice's until June: neither is in her rows, her total, or reachable by id.
        Assert.DoesNotContain(alice.Rows, r => r.PropertyId == world.Altrui.Id || r.PropertyId == world.Venduta.Id);
        Assert.Equal(750m, alice.Total.Figures.Revenue);

        foreach (var notHers in new[] { world.Altrui.Id, world.Venduta.Id, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<NotFoundException>(() =>
                world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query() with { PropertyId = notHers }));
            await Assert.ThrowsAsync<NotFoundException>(() =>
                world.Service.GetOwnerTrendAsync(world.Alice.Id, Query() with { PropertyId = notHers }));
        }

        // Her own property by id is a view of just that one; Carol sees only hers, with all of it.
        var sola = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query() with { PropertyId = world.Sola.Id });
        Assert.Equal(world.Sola.Id, Assert.Single(sola.Rows).PropertyId);
        Assert.Equal(200m, sola.Total.Figures.Revenue);
        var carol = await world.Service.GetOwnerOverviewAsync(world.Carol.Id, Query());
        Assert.Equal(world.Altrui.Id, Assert.Single(carol.Rows).PropertyId);
        Assert.Equal(500m, carol.Total.Figures.Revenue);

        // An owner with no ownership at all has nothing: no rows, no money, no made-up year before.
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
        // Alice bought Casa Appena Comprata on 5 October: it is not hers in September, and it is in October.
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

    // ---------------------------------------------------------------- proof: no guest field in the answers

    [Fact]
    public async Task OwnerPortalOverview_NoGuestData_Test()
    {
        var world = await SeedAsync();

        var overview = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query(compare: true));
        var trend = await world.Service.GetOwnerTrendAsync(world.Alice.Id, Query(compare: true));
        var overviewJson = JsonSerializer.Serialize(OwnerPortalOverviewDto.From(overview, HostPerformanceGroupBy.Property, "Prestazioni"));
        var trendJson = JsonSerializer.Serialize(OwnerPortalTrendDto.From(trend, HostPerformanceGroupBy.Property, "Andamento"));

        // The stays of the figures belong to guests with a name, an e-mail and a phone: none of it is in the answers, and neither is the
        // data of another owner (their names, their e-mails).
        foreach (var json in new[] { overviewJson, trendJson })
        {
            foreach (var secret in new[] { GuestName, GuestSurname, GuestEmail, GuestPhone, world.Bob.Email, world.Carol.Email, world.Bob.Auth0Sub })
                Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("Altrui", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Venduta", json, StringComparison.Ordinal);
        }

        Assert.Contains("Casa Sola", overviewJson, StringComparison.Ordinal);

        // The shape cannot carry it: no member of the results or of the answers is about a guest, an e-mail or a phone.
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

    // ---------------------------------------------------------------- the trend and the property list

    [Fact]
    public async Task OwnerPortalTrend_WeightedBucketsAddUpToTheOverview_WithTheYearBefore()
    {
        var world = await SeedAsync();
        var overview = await world.Service.GetOwnerOverviewAsync(world.Alice.Id, Query(HostPerformanceGroupBy.None, compare: true));

        var together = await world.Service.GetOwnerTrendAsync(world.Alice.Id, Query(HostPerformanceGroupBy.None, compare: true));

        // The host's weeks (1 to 4, three whole ones, 26 to 31 October), and the lines weighted add up to the overview.
        Assert.Equal(HostOverviewSeriesUnit.Week, together.Unit);
        Assert.Equal(5, together.Buckets.Count);
        var line = Assert.Single(together.Series);
        Assert.Equal(HostPerformanceRules.TotalKey, line.Key);
        Assert.Equal(overview.Total.Figures.Revenue, Math.Round(line.Points.Sum(p => p.Revenue), 2));
        Assert.Equal(overview.Total.Figures.OccupiedNights, line.Points.Sum(p => p.OccupiedNights));
        Assert.Equal(overview.Total.Figures.AvailableNights, line.Points.Sum(p => p.AvailableNights));
        Assert.Equal(5, line.PreviousYear.Count);
        Assert.Equal(150m, Math.Round(line.PreviousYear.Sum(p => p!.Revenue), 2));
        Assert.Equal(150m, line.PreviousYear[2]!.Revenue);   // the stay of 14 to 17 October 2025 is in the third week of that month

        // One line for each property, the most revenue first (Villa Due 350, then Casa Sola and the half of the villa, 200 each).
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

        // The last 30 days up to 9 October: 10 of the 30 nights of Casa Sola (20 to 29 September), 3 of Villa Due (3, 4, 5 October), none of
        // the villa (its stay is later). A percentage from 0 to 100 to one decimal.
        Assert.Equal(33.3, properties.Single(p => p.PropertyId == world.Sola.Id).OccupancyRate);
        Assert.Equal(10.0, properties.Single(p => p.PropertyId == world.VillaDue.Id).OccupancyRate);
        Assert.Equal(0.0, properties.Single(p => p.PropertyId == world.Villa.Id).OccupancyRate);
        Assert.DoesNotContain(properties, p => p.PropertyId == world.Venduta.Id);

        // Built without the Home (as older code does) the older reads still work, and the new ones say what they need.
        var withoutHome = new OwnerPortalService(HostScopeScenario.NewInMemoryDb(), Clock);
        Assert.Empty(await withoutHome.GetOwnerPropertiesAsync(world.Alice.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutHome.GetOwnerOverviewAsync(world.Alice.Id, Query()));
    }
}
