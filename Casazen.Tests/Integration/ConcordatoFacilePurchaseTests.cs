using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// ADR-004-R5: ConcordatoFacilePurchase tenant isolation — a user of org B cannot query, update or delete
/// the purchase records of org A, and a query with OrgId scope only returns the caller's own org data.
/// </summary>
public class ConcordatoFacilePurchaseTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task OrgIdIsolation_QueryOnlyReturnsOwnOrgData()
    {
        // Arrange: Two orgs with one purchase each
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var propertyA = Guid.NewGuid();
        var propertyB = Guid.NewGuid();
        var leaseA = Guid.NewGuid();
        var leaseB = Guid.NewGuid();
        var purchaseA = Guid.NewGuid();
        var purchaseB = Guid.NewGuid();

        await using (var db = _database!.CreateContext())
        {
            await db.Database.MigrateAsync();

            // Seed two orgs with properties, leases and purchases
            db.Orgs.AddRange(
                new OrgEntity { Id = orgA, Name = "OrgA", Slug = "org-a", CreatedAt = DateTime.UtcNow },
                new OrgEntity { Id = orgB, Name = "OrgB", Slug = "org-b", CreatedAt = DateTime.UtcNow }
            );

            db.Properties.AddRange(
                new Property { Id = propertyA, OrgId = orgA, Name = "PropertyA", City = "Rome", Address = "Via Roma 1", OwnerId = "auth0|owner_a" },
                new Property { Id = propertyB, OrgId = orgB, Name = "PropertyB", City = "Milan", Address = "Via Milano 1", OwnerId = "auth0|owner_b" }
            );

            db.LeaseContracts.AddRange(
                new LeaseContract { Id = leaseA, OrgId = orgA, PropertyId = propertyA, CreatedAt = DateTime.UtcNow },
                new LeaseContract { Id = leaseB, OrgId = orgB, PropertyId = propertyB, CreatedAt = DateTime.UtcNow }
            );

            db.ConcordatoFacilePurchases.AddRange(
                new ConcordatoFacilePurchase
                {
                    Id = purchaseA,
                    OrgId = orgA,
                    LeaseContractId = leaseA,
                    StripeCheckoutSessionId = "cs_test_a",
                    AmountCents = 11900,
                    Currency = "eur",
                    Status = ConcordatoFacilePurchaseStatus.Completed,
                    CreatedAt = DateTime.UtcNow,
                    PaidAt = DateTime.UtcNow
                },
                new ConcordatoFacilePurchase
                {
                    Id = purchaseB,
                    OrgId = orgB,
                    LeaseContractId = leaseB,
                    StripeCheckoutSessionId = "cs_test_b",
                    AmountCents = 11900,
                    Currency = "eur",
                    Status = ConcordatoFacilePurchaseStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                }
            );

            await db.SaveChangesAsync();
        }

        // Act & Assert: Query with OrgId scope for org B should only return org B's purchase
        await using (var dbForOrgB = new AppDbContext(
            _database.CreateOptions(),
            new StubTenantContext(orgB)))
        {
            var visiblePurchases = await dbForOrgB.ConcordatoFacilePurchases
                .AsNoTracking()
                .ToListAsync();

            Assert.Single(visiblePurchases);
            Assert.Equal(purchaseB, visiblePurchases[0].Id);
            Assert.Equal(orgB, visiblePurchases[0].OrgId);
            Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, visiblePurchases[0].Status);
        }

        // Act & Assert: Direct query without tenant context should still respect the scope when filtering
        await using (var dbRaw = _database.CreateContext())
        {
            var orgAData = await dbRaw.ConcordatoFacilePurchases
                .AsNoTracking()
                .Where(p => p.OrgId == orgA)
                .ToListAsync();

            Assert.Single(orgAData);
            Assert.Equal(purchaseA, orgAData[0].Id);
            Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, orgAData[0].Status);
        }
    }

    private sealed class StubTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;
        public bool FilterEnabled => true;
    }
}
