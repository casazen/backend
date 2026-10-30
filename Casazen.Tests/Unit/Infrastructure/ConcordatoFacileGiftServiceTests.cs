using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Tests for ConcordatoFacile gift service (ADR-006).
/// </summary>
public sealed class ConcordatoFacileGiftServiceTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task GrantGiftAsync_NewGift_CreatesGiftWithAvailableStatus()
    {
        // Arrange
        Guid orgId;
        await using (var db = _database!.CreateContext())
        {
            var org = new OrgEntity
            {
                Name = $"Test Org {Guid.NewGuid()}",
                Slug = $"test-org-{Guid.NewGuid():N}",
                CreatedAt = DateTime.UtcNow
            };
            db.Orgs.Add(org);
            await db.SaveChangesAsync();
            orgId = org.Id;
        }

        var subscriptionId = "sub_test_biennial";

        // Act
        await using (var db = _database.CreateContext())
        {
            var service = new ConcordatoFacileGiftService(db, NullLogger<ConcordatoFacileGiftService>.Instance);
            await service.GrantGiftAsync(orgId, subscriptionId);
        }

        // Assert
        await using (var db = _database.CreateContext())
        {
            var gift = await db.ConcordatoFacileGifts
                .FirstOrDefaultAsync(g => g.OrgId == orgId);

            Assert.NotNull(gift);
            Assert.Equal(orgId, gift.OrgId);
            Assert.Equal(subscriptionId, gift.GrantedBySubscriptionId);
            Assert.Equal(ConcordatoFacileGiftStatus.Available, gift.Status);
            Assert.Null(gift.ConsumedAt);
            Assert.Null(gift.ConsumedForLeaseId);
        }
    }

    [PostgresFact]
    public async Task GrantGiftAsync_GiftAlreadyExists_IsIdempotent()
    {
        // Arrange
        Guid orgId;
        await using (var db = _database!.CreateContext())
        {
            var org = new OrgEntity { Name = "Test", Slug = $"test-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Orgs.Add(org);
            await db.SaveChangesAsync();
            orgId = org.Id;
        }

        var firstSubscriptionId = "sub_first";
        var secondSubscriptionId = "sub_second";

        await using (var db = _database.CreateContext())
        {
            var service = new ConcordatoFacileGiftService(db, NullLogger<ConcordatoFacileGiftService>.Instance);
            await service.GrantGiftAsync(orgId, firstSubscriptionId);
        }

        // Act
        await using (var db = _database.CreateContext())
        {
            var service = new ConcordatoFacileGiftService(db, NullLogger<ConcordatoFacileGiftService>.Instance);
            await service.GrantGiftAsync(orgId, secondSubscriptionId);
        }

        // Assert
        await using (var db = _database.CreateContext())
        {
            var gifts = await db.ConcordatoFacileGifts
                .Where(g => g.OrgId == orgId)
                .ToListAsync();

            Assert.Single(gifts);
            Assert.Equal(firstSubscriptionId, gifts[0].GrantedBySubscriptionId);
        }
    }

    /// <summary>
    /// Proof test for concurrency control (ADR-006-R4, ADR-006-R5).
    /// Runs 5 concurrent attempts to consume the same gift; only one should succeed.
    /// </summary>
    [PostgresFact]
    public async Task ConsumeGift_ConcurrentRequests_OnlyOneSucceeds()
    {
        // Arrange
        Guid orgId;
        var leaseIds = new List<Guid>();

        await using (var db = _database!.CreateContext())
        {
            var org = new OrgEntity { Name = "Test", Slug = $"test-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Orgs.Add(org);
            await db.SaveChangesAsync();
            orgId = org.Id;

            // Create a property for the leases
            var property = new Property
            {
                OrgId = orgId,
                Name = "Test Property",
                City = "Rome",
                Address = "Via Test 1",
                OwnerId = "auth0|test_owner"
            };
            db.Properties.Add(property);
            await db.SaveChangesAsync();

            // Create 5 lease contracts to satisfy the FK constraint
            for (int i = 0; i < 5; i++)
            {
                var lease = new LeaseContract
                {
                    OrgId = orgId,
                    PropertyId = property.Id,
                    CreatedAt = DateTime.UtcNow
                };
                db.LeaseContracts.Add(lease);
                leaseIds.Add(lease.Id);
            }
            await db.SaveChangesAsync();
        }

        await using (var db = _database.CreateContext())
        {
            var service = new ConcordatoFacileGiftService(db, NullLogger<ConcordatoFacileGiftService>.Instance);
            await service.GrantGiftAsync(orgId, "sub_test_concurrent");
        }

        // Act - 5 concurrent consumption attempts for different leases
        var tasks = leaseIds.Select(leaseId =>
            Task.Run(async () =>
            {
                await using var context = _database.CreateContext();
                var service = new ConcordatoFacileGiftService(context, NullLogger<ConcordatoFacileGiftService>.Instance);
                return await service.ConsumeGiftAsync(orgId, leaseId);
            })).ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - exactly one Success, the rest AlreadyConsumedForOtherLease
        var successCount = results.Count(r => r == CfGiftConsumeResult.Success);
        var failureCount = results.Count(r => r == CfGiftConsumeResult.AlreadyConsumedForOtherLease);

        Assert.Equal(1, successCount);
        Assert.Equal(4, failureCount);

        // Verify the gift is consumed for exactly one lease
        await using (var db = _database.CreateContext())
        {
            var gift = await db.ConcordatoFacileGifts.FirstAsync(g => g.OrgId == orgId);

            Assert.Equal(ConcordatoFacileGiftStatus.Consumed, gift.Status);
            Assert.NotNull(gift.ConsumedForLeaseId);
            Assert.Contains(gift.ConsumedForLeaseId.Value, leaseIds);
        }
    }

    /// <summary>
    /// Proof test for tenant isolation (ADR-006-R5, CF-FIX2). An org's gift is spent only on a lease of that org: a lease that belongs to
    /// another org, or that does not exist, is a 404 <c>lease_not_found</c>, the gift stays available and nothing is written (not even the
    /// event on the other org's lease); on its own lease the same gift is then consumed.
    /// </summary>
    [PostgresFact]
    public async Task ConsumeGift_LeaseOfAnotherOrgOrMissing_Is404_AndTheGiftIsUntouched()
    {
        // Arrange: org1 has a gift and a lease, org2 has a lease
        Guid org1Id, org2Id, leaseOfOrg1Id, leaseOfOrg2Id;

        await using (var db = _database!.CreateContext())
        {
            var org1 = new OrgEntity { Name = "Org1", Slug = $"org1-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            var org2 = new OrgEntity { Name = "Org2", Slug = $"org2-{Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            db.Orgs.Add(org1);
            db.Orgs.Add(org2);
            await db.SaveChangesAsync();

            org1Id = org1.Id;
            org2Id = org2.Id;

            var property1 = new Property
            {
                OrgId = org1Id,
                Name = "Test Property",
                City = "Rome",
                Address = "Via Test 1",
                OwnerId = "auth0|test_owner"
            };
            var property2 = new Property
            {
                OrgId = org2Id,
                Name = "Test Property",
                City = "Rome",
                Address = "Via Test 2",
                OwnerId = "auth0|test_owner"
            };
            db.Properties.Add(property1);
            db.Properties.Add(property2);
            await db.SaveChangesAsync();

            var leaseOfOrg1 = new LeaseContract { OrgId = org1Id, PropertyId = property1.Id, CreatedAt = DateTime.UtcNow };
            var leaseOfOrg2 = new LeaseContract { OrgId = org2Id, PropertyId = property2.Id, CreatedAt = DateTime.UtcNow };
            db.LeaseContracts.Add(leaseOfOrg1);
            db.LeaseContracts.Add(leaseOfOrg2);
            await db.SaveChangesAsync();

            leaseOfOrg1Id = leaseOfOrg1.Id;
            leaseOfOrg2Id = leaseOfOrg2.Id;
        }

        await using (var db = _database.CreateContext())
        {
            var service = new ConcordatoFacileGiftService(db, NullLogger<ConcordatoFacileGiftService>.Instance);
            await service.GrantGiftAsync(org1Id, "sub_org1");
        }

        // Act: org1 tries to spend its gift on org2's lease and on a lease that does not exist
        await using (var db = _database.CreateContext())
        {
            var service = new ConcordatoFacileGiftService(db, NullLogger<ConcordatoFacileGiftService>.Instance);

            var other = await Assert.ThrowsAsync<NotFoundException>(() => service.ConsumeGiftAsync(org1Id, leaseOfOrg2Id));
            var missing = await Assert.ThrowsAsync<NotFoundException>(() => service.ConsumeGiftAsync(org1Id, Guid.NewGuid()));

            Assert.Equal("lease_not_found", other.Code);
            Assert.Equal("lease_not_found", missing.Code);
        }

        // Assert: the gift is still available and nothing was recorded on org2's lease
        await using (var db = _database.CreateContext())
        {
            var gift = await db.ConcordatoFacileGifts.FirstAsync(g => g.OrgId == org1Id);

            Assert.Equal(ConcordatoFacileGiftStatus.Available, gift.Status);
            Assert.Null(gift.ConsumedForLeaseId);
            Assert.False(await db.LeaseEvents.AnyAsync(e => e.LeaseContractId == leaseOfOrg2Id));
        }

        // On org1's own lease the same gift is consumed
        await using (var db = _database.CreateContext())
        {
            var service = new ConcordatoFacileGiftService(db, NullLogger<ConcordatoFacileGiftService>.Instance);

            Assert.Equal(CfGiftConsumeResult.Success, await service.ConsumeGiftAsync(org1Id, leaseOfOrg1Id));
        }

        await using (var db = _database.CreateContext())
        {
            var gift = await db.ConcordatoFacileGifts.FirstAsync(g => g.OrgId == org1Id);

            Assert.Equal(ConcordatoFacileGiftStatus.Consumed, gift.Status);
            Assert.Equal(leaseOfOrg1Id, gift.ConsumedForLeaseId);
        }
    }
}
