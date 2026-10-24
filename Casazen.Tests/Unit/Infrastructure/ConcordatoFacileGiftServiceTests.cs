using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Tests for ConcordatoFacile gift service (ADR-006).
/// </summary>
public sealed class ConcordatoFacileGiftServiceTests : IAsyncLifetime
{
    private readonly PostgresTestDatabase _testDb = new();
    private AppDbContext _dbContext = null!;
    private ConcordatoFacileGiftService _service = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _dbContext = _testDb.CreateContext();
        _service = new ConcordatoFacileGiftService(_dbContext, NullLogger<ConcordatoFacileGiftService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _testDb.DisposeAsync();
    }

    [PostgresFact]
    public async Task GrantGiftAsync_NewGift_CreatesGiftWithAvailableStatus()
    {
        // Arrange
        var org = await CreateOrgAsync();
        var subscriptionId = "sub_test_biennial";

        // Act
        await _service.GrantGiftAsync(org.Id, subscriptionId);

        // Assert
        var gift = await _dbContext.ConcordatoFacileGifts
            .FirstOrDefaultAsync(g => g.OrgId == org.Id);

        Assert.NotNull(gift);
        Assert.Equal(org.Id, gift.OrgId);
        Assert.Equal(subscriptionId, gift.GrantedBySubscriptionId);
        Assert.Equal(ConcordatoFacileGiftStatus.Available, gift.Status);
        Assert.Null(gift.ConsumedAt);
        Assert.Null(gift.ConsumedForLeaseId);
    }

    [PostgresFact]
    public async Task GrantGiftAsync_GiftAlreadyExists_IsIdempotent()
    {
        // Arrange
        var org = await CreateOrgAsync();
        var firstSubscriptionId = "sub_first";
        var secondSubscriptionId = "sub_second";

        await _service.GrantGiftAsync(org.Id, firstSubscriptionId);

        // Act
        await _service.GrantGiftAsync(org.Id, secondSubscriptionId);

        // Assert
        var gifts = await _dbContext.ConcordatoFacileGifts
            .Where(g => g.OrgId == org.Id)
            .ToListAsync();

        Assert.Single(gifts);
        Assert.Equal(firstSubscriptionId, gifts[0].GrantedBySubscriptionId);
    }

    [PostgresFact]
    public async Task ConsumeGiftAsync_AvailableGift_ConsumesSuccessfully()
    {
        // Arrange
        var org = await CreateOrgAsync();
        var lease = await CreateLeaseAsync(org.Id);
        await _service.GrantGiftAsync(org.Id, "sub_test");

        // Act
        var result = await _service.ConsumeGiftAsync(org.Id, lease.Id);

        // Assert
        Assert.Equal(CfGiftConsumeResult.Success, result);

        var gift = await _dbContext.ConcordatoFacileGifts
            .FirstAsync(g => g.OrgId == org.Id);

        Assert.Equal(ConcordatoFacileGiftStatus.Consumed, gift.Status);
        Assert.Equal(lease.Id, gift.ConsumedForLeaseId);
        Assert.NotNull(gift.ConsumedAt);

        // Verify LeaseEvent was recorded (ADR-006-R7)
        var leaseEvent = await _dbContext.LeaseEvents
            .FirstOrDefaultAsync(e => e.LeaseContractId == lease.Id
                && e.EventType == LeaseEventType.ConcordatoFacileGiftConsumed);

        Assert.NotNull(leaseEvent);
    }

    [PostgresFact]
    public async Task ConsumeGiftAsync_SameLeaseIdTwice_ReturnsAlreadyConsumedForThisLease()
    {
        // Arrange
        var org = await CreateOrgAsync();
        var lease = await CreateLeaseAsync(org.Id);
        await _service.GrantGiftAsync(org.Id, "sub_test");

        await _service.ConsumeGiftAsync(org.Id, lease.Id);

        // Act - second consumption for same lease (re-entry)
        var result = await _service.ConsumeGiftAsync(org.Id, lease.Id);

        // Assert
        Assert.Equal(CfGiftConsumeResult.AlreadyConsumedForThisLease, result);
    }

    [PostgresFact]
    public async Task ConsumeGiftAsync_DifferentLeaseId_ReturnsAlreadyConsumedForOtherLease()
    {
        // Arrange
        var org = await CreateOrgAsync();
        var firstLease = await CreateLeaseAsync(org.Id);
        var secondLease = await CreateLeaseAsync(org.Id);
        await _service.GrantGiftAsync(org.Id, "sub_test");

        await _service.ConsumeGiftAsync(org.Id, firstLease.Id);

        // Act - try to consume for different lease
        var result = await _service.ConsumeGiftAsync(org.Id, secondLease.Id);

        // Assert
        Assert.Equal(CfGiftConsumeResult.AlreadyConsumedForOtherLease, result);
    }

    /// <summary>
    /// Proof test for concurrency control (ADR-006-R4, ADR-006-R5).
    /// Runs 5 concurrent attempts to consume the same gift; only one should succeed.
    /// </summary>
    [PostgresFact]
    public async Task ConsumeGift_ConcurrentRequests_OnlyOneSucceeds()
    {
        // Arrange
        var org = await CreateOrgAsync();
        var leases = new List<LeaseContract>();
        for (int i = 0; i < 5; i++)
        {
            leases.Add(await CreateLeaseAsync(org.Id));
        }
        await _service.GrantGiftAsync(org.Id, "sub_test_concurrent");

        // Act - 5 concurrent consumption attempts for different leases
        var tasks = leases.Select(lease =>
            Task.Run(async () =>
            {
                await using var context = _testDb.CreateContext();
                var service = new ConcordatoFacileGiftService(context, NullLogger<ConcordatoFacileGiftService>.Instance);
                return await service.ConsumeGiftAsync(org.Id, lease.Id);
            })).ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - exactly one Success, the rest AlreadyConsumedForOtherLease
        var successCount = results.Count(r => r == CfGiftConsumeResult.Success);
        var failureCount = results.Count(r => r == CfGiftConsumeResult.AlreadyConsumedForOtherLease);

        Assert.Equal(1, successCount);
        Assert.Equal(4, failureCount);

        // Verify the gift is consumed for exactly one lease
        var gift = await _dbContext.ConcordatoFacileGifts
            .FirstAsync(g => g.OrgId == org.Id);

        Assert.Equal(ConcordatoFacileGiftStatus.Consumed, gift.Status);
        Assert.NotNull(gift.ConsumedForLeaseId);
        Assert.Contains(gift.ConsumedForLeaseId.Value, leases.Select(l => l.Id));
    }

    /// <summary>
    /// Proof test for tenant isolation (ADR-006-R5).
    /// An org's gift cannot be consumed for another org's lease.
    /// </summary>
    [PostgresFact]
    public async Task ConsumeGift_OrgIdMismatch_Fails()
    {
        // Arrange
        var org1 = await CreateOrgAsync();
        var org2 = await CreateOrgAsync();
        var leaseOfOrg2 = await CreateLeaseAsync(org2.Id);

        await _service.GrantGiftAsync(org1.Id, "sub_org1");

        // Act - try to consume org1's gift for org2's lease
        var result = await _service.ConsumeGiftAsync(org1.Id, leaseOfOrg2.Id);

        // Assert - should fail because the lease belongs to a different org
        // The gift remains available for org1
        Assert.Equal(CfGiftConsumeResult.AlreadyConsumedForOtherLease, result);

        var gift = await _dbContext.ConcordatoFacileGifts
            .FirstAsync(g => g.OrgId == org1.Id);

        Assert.Equal(ConcordatoFacileGiftStatus.Available, gift.Status);
        Assert.Null(gift.ConsumedForLeaseId);
    }

    private async Task<Org> CreateOrgAsync()
    {
        var org = new Org
        {
            Name = $"Test Org {Guid.NewGuid()}",
            Slug = $"test-org-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Orgs.Add(org);
        await _dbContext.SaveChangesAsync();
        return org;
    }

    private async Task<LeaseContract> CreateLeaseAsync(Guid orgId)
    {
        // Create a minimal property
        var property = new Property
        {
            OrgId = orgId,
            Name = $"Test Property {Guid.NewGuid()}",
            Address = "Test Address",
            City = "Test City",
            PostalCode = "12345",
            Country = "IT",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _dbContext.Properties.Add(property);

        var lease = new LeaseContract
        {
            OrgId = orgId,
            PropertyId = property.Id,
            LeaseType = LeaseType.Concordato,
            Status = LeaseStatus.Draft,
            StartDate = DateTime.UtcNow.AddDays(30),
            EndDate = DateTime.UtcNow.AddDays(30).AddMonths(24),
            MonthlyRent = 500m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.LeaseContracts.Add(lease);
        await _dbContext.SaveChangesAsync();
        return lease;
    }
}
