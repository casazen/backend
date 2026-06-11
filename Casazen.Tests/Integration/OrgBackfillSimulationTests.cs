using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AC4/AC10 — backfill mapping rules. The production backfill ships as raw PostgreSQL in the
/// <c>BackfillDefaultOrgs</c> migration (validated provider-accurately in <c>MigrationSqlTests</c>).
/// Because the test harness runs on the EF in-memory provider (no Postgres/Docker), this test
/// mirrors the migration's logical steps (one Org per distinct OwnerId → set User.OrgId →
/// relationship-walk OrgId onto Property/Booking/Payment/Lease) and asserts the invariants the
/// design's AC10 regression guarantees: each owner's rows land under their own default Org, no
/// orphans remain, distinct owners never share an Org, and the operation is idempotent.
/// <c>Guid.Empty</c> stands in for the pre-migration "NULL OrgId".
/// </summary>
public class OrgBackfillSimulationTests
{
    private const string OwnerA = "auth0|owner-a";
    private const string OwnerB = "auth0|owner-b";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"backfill-{Guid.NewGuid()}")
            .Options);

    private static async Task SeedPreMigrationStateAsync(AppDbContext db)
    {
        db.Users.AddRange(
            new User { Id = OwnerA, Email = "a@x.it", FirstName = "A", LastName = "Owner" },
            new User { Id = OwnerB, Email = "b@x.it", FirstName = "B", LastName = "Owner" });

        var a1 = new Property { Id = Guid.NewGuid(), OwnerId = OwnerA, OrgId = Guid.Empty, Name = "A1", Address = "A", City = "Rome" };
        var a2 = new Property { Id = Guid.NewGuid(), OwnerId = OwnerA, OrgId = Guid.Empty, Name = "A2", Address = "A", City = "Rome" };
        var b1 = new Property { Id = Guid.NewGuid(), OwnerId = OwnerB, OrgId = Guid.Empty, Name = "B1", Address = "A", City = "Milan" };
        db.Properties.AddRange(a1, a2, b1);

        var bookingA = new Booking { Id = Guid.NewGuid(), PropertyId = a1.Id, GuestId = Guid.NewGuid(), OrgId = Guid.Empty };
        var bookingB = new Booking { Id = Guid.NewGuid(), PropertyId = b1.Id, GuestId = Guid.NewGuid(), OrgId = Guid.Empty };
        db.Bookings.AddRange(bookingA, bookingB);

        db.Payments.Add(new Payment { Id = Guid.NewGuid(), BookingId = bookingA.Id, OrgId = Guid.Empty, Amount = 100m });
        db.LeaseContracts.Add(new LeaseContract { Id = Guid.NewGuid(), PropertyId = b1.Id, OrgId = Guid.Empty, MonthlyRent = 500m });

        await db.SaveChangesAsync();
    }

    /// <summary>Mirrors <c>BackfillDefaultOrgs</c> Up steps 1–4. Returns orgs created this run.</summary>
    private static async Task RunBackfillAsync(AppDbContext db)
    {
        // 1) One default Org per distinct Property.OwnerId (idempotent on slug).
        var owners = await db.Properties.Select(p => p.OwnerId).Distinct().ToListAsync();
        foreach (var owner in owners)
        {
            var slug = $"org-{owner}";
            if (!await db.Orgs.AnyAsync(o => o.Slug == slug))
            {
                db.Orgs.Add(new OrgEntity { Name = owner, Slug = slug, DisplayName = owner, ContactEmail = "", PlanTier = PlanTier.Starter, IsActive = true });
            }
        }
        await db.SaveChangesAsync();

        var orgBySlug = await db.Orgs.ToDictionaryAsync(o => o.Slug, o => o.Id);

        // 2) Link each owner's User row to their Org.
        foreach (var user in await db.Users.Where(u => u.OrgId == null).ToListAsync())
        {
            if (orgBySlug.TryGetValue($"org-{user.Id}", out var orgId))
                user.OrgId = orgId;
        }

        // 3) Relationship walk (only the unassigned sentinel).
        foreach (var p in await db.Properties.Where(p => p.OrgId == Guid.Empty).ToListAsync())
            p.OrgId = orgBySlug[$"org-{p.OwnerId}"];
        await db.SaveChangesAsync();

        var propOrg = await db.Properties.ToDictionaryAsync(p => p.Id, p => p.OrgId);
        foreach (var b in await db.Bookings.Where(b => b.OrgId == Guid.Empty).ToListAsync())
            b.OrgId = propOrg[b.PropertyId];

        var bookingOrg = await db.Bookings.ToDictionaryAsync(b => b.Id, b => b.OrgId);
        foreach (var pay in await db.Payments.Where(p => p.OrgId == Guid.Empty).ToListAsync())
            pay.OrgId = bookingOrg[pay.BookingId];

        foreach (var l in await db.LeaseContracts.Where(l => l.OrgId == Guid.Empty).ToListAsync())
            l.OrgId = propOrg[l.PropertyId];

        // 4) Fallback Org (quarantine target).
        if (!await db.Orgs.AnyAsync(o => o.Slug == "casazen-unassigned"))
            db.Orgs.Add(new OrgEntity { Name = "CasaZen Unassigned", Slug = "casazen-unassigned", DisplayName = "CasaZen Unassigned", ContactEmail = "", PlanTier = PlanTier.Starter, IsActive = false });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Backfill_CreatesOneOrgPerOwner_AndAssignsRowsByRelationshipWalk()
    {
        await using var db = NewDb();
        await SeedPreMigrationStateAsync(db);

        await RunBackfillAsync(db);

        // One default org per distinct owner (2) + the fallback org.
        Assert.Equal(2, await db.Orgs.CountAsync(o => o.Slug.StartsWith("org-")));
        Assert.True(await db.Orgs.AnyAsync(o => o.Slug == "casazen-unassigned"));

        var orgA = (await db.Users.SingleAsync(u => u.Id == OwnerA)).OrgId;
        var orgB = (await db.Users.SingleAsync(u => u.Id == OwnerB)).OrgId;
        Assert.NotNull(orgA);
        Assert.NotNull(orgB);
        Assert.NotEqual(orgA, orgB); // distinct owners never share an org (no cross-owner leak)

        // Every Property/Booking/Payment/Lease assigned to the correct owner's org.
        Assert.All(await db.Properties.Where(p => p.OwnerId == OwnerA).ToListAsync(), p => Assert.Equal(orgA, p.OrgId));
        Assert.All(await db.Properties.Where(p => p.OwnerId == OwnerB).ToListAsync(), p => Assert.Equal(orgB, p.OrgId));
        Assert.Equal(orgA, (await db.Payments.SingleAsync()).OrgId);       // payment→booking→A1→orgA
        Assert.Equal(orgB, (await db.LeaseContracts.SingleAsync()).OrgId); // lease→B1→orgB
    }

    [Fact]
    public async Task Backfill_LeavesNoOrphans()
    {
        await using var db = NewDb();
        await SeedPreMigrationStateAsync(db);

        await RunBackfillAsync(db);

        // AC10b pre-flight precondition: zero tenant-scoped rows remain unassigned.
        Assert.False(await db.Properties.AnyAsync(p => p.OrgId == Guid.Empty));
        Assert.False(await db.Bookings.AnyAsync(b => b.OrgId == Guid.Empty));
        Assert.False(await db.Payments.AnyAsync(p => p.OrgId == Guid.Empty));
        Assert.False(await db.LeaseContracts.AnyAsync(l => l.OrgId == Guid.Empty));
    }

    [Fact]
    public async Task Backfill_IsIdempotent()
    {
        await using var db = NewDb();
        await SeedPreMigrationStateAsync(db);

        await RunBackfillAsync(db);
        var orgsAfterFirst = await db.Orgs.CountAsync();
        var assignmentsAfterFirst = await db.Properties.Select(p => new { p.Id, p.OrgId }).ToListAsync();

        await RunBackfillAsync(db); // re-run

        Assert.Equal(orgsAfterFirst, await db.Orgs.CountAsync()); // no duplicate orgs
        var assignmentsAfterSecond = await db.Properties.Select(p => new { p.Id, p.OrgId }).ToListAsync();
        Assert.Equal(assignmentsAfterFirst.OrderBy(x => x.Id), assignmentsAfterSecond.OrderBy(x => x.Id));
    }
}
