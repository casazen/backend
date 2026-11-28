using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class ConcordatoFacileAccessServiceTests
{
    [Fact]
    public async Task CheckAccess_PaymentCompleted_ReturnsHasAccessTrue()
    {
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        var pendingPurchase = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = "cs_test_pending",
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        db.ConcordatoFacilePurchases.Add(pendingPurchase);
        await db.SaveChangesAsync();

        var tenantContext = new TestTenantContext(orgId);
        var logger = NullLogger<ConcordatoFacileAccessService>.Instance;
        var service = new ConcordatoFacileAccessService(db, tenantContext, logger);
        var user = new ClaimsPrincipal();

        var resultPending = await service.CheckAccessAsync(leaseId, user);

        Assert.False(resultPending.HasAccess);
        Assert.Equal(CfAccessSource.None, resultPending.Source);

        pendingPurchase.Status = ConcordatoFacilePurchaseStatus.Completed;
        pendingPurchase.PaidAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var resultCompleted = await service.CheckAccessAsync(leaseId, user);

        Assert.True(resultCompleted.HasAccess);
        Assert.Equal(CfAccessSource.Payment, resultCompleted.Source);
        Assert.NotNull(resultCompleted.PurchasedAt);
    }

    [Fact]
    public async Task CheckAccess_OtherOrgPurchase_ReturnsHasAccessFalse()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        var purchaseOrgA = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgA,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = "cs_test_orgA",
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            PaidAt = DateTime.UtcNow
        };

        db.ConcordatoFacilePurchases.Add(purchaseOrgA);
        await db.SaveChangesAsync();

        var tenantContextOrgB = new TestTenantContext(orgB);
        var logger = NullLogger<ConcordatoFacileAccessService>.Instance;
        var service = new ConcordatoFacileAccessService(db, tenantContextOrgB, logger);
        var user = new ClaimsPrincipal();

        var result = await service.CheckAccessAsync(leaseId, user);

        Assert.False(result.HasAccess);
        Assert.Equal(CfAccessSource.None, result.Source);

        // PROOF: The EF global query filter (ITenantOwned) prevents cross-org access.
        // If tenant isolation failed, org B would see org A's purchase and get access.
        // This test proves the service respects tenant boundaries (data rule).
    }

    [Fact]
    public async Task CheckAccess_AdminRole_ReturnsHasAccessTrue()
    {
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();
        var tenantContext = new TestTenantContext(orgId);
        var logger = NullLogger<ConcordatoFacileAccessService>.Instance;
        var service = new ConcordatoFacileAccessService(db, tenantContext, logger);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("sub", "admin-user-123")
        });
        var user = new ClaimsPrincipal(identity);

        var result = await service.CheckAccessAsync(leaseId, user);

        Assert.True(result.HasAccess);
        Assert.Equal(CfAccessSource.Admin, result.Source);
    }

    [Fact]
    public async Task CheckAccess_GiftConsumed_ReturnsHasAccessTrue()
    {
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        var gift = new ConcordatoFacileGift
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            GrantedBySubscriptionId = "sub_test_123",
            GrantedAt = DateTime.UtcNow.AddDays(-1),
            ConsumedAt = DateTime.UtcNow,
            ConsumedForLeaseId = leaseId,
            Status = ConcordatoFacileGiftStatus.Consumed
        };

        db.ConcordatoFacileGifts.Add(gift);
        await db.SaveChangesAsync();

        var tenantContext = new TestTenantContext(orgId);
        var logger = NullLogger<ConcordatoFacileAccessService>.Instance;
        var service = new ConcordatoFacileAccessService(db, tenantContext, logger);
        var user = new ClaimsPrincipal();

        var result = await service.CheckAccessAsync(leaseId, user);

        Assert.True(result.HasAccess);
        Assert.Equal(CfAccessSource.Gift, result.Source);
        Assert.True(result.GiftConsumed);
        Assert.NotNull(result.GiftAssignedAt);
    }

    [Fact]
    public async Task CheckAccess_AvailableGift_ReturnsGiftAvailableTrue()
    {
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        var availableGift = new ConcordatoFacileGift
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            GrantedBySubscriptionId = "sub_test_456",
            GrantedAt = DateTime.UtcNow,
            ConsumedAt = null,
            ConsumedForLeaseId = null,
            Status = ConcordatoFacileGiftStatus.Available
        };

        db.ConcordatoFacileGifts.Add(availableGift);
        await db.SaveChangesAsync();

        var tenantContext = new TestTenantContext(orgId);
        var logger = NullLogger<ConcordatoFacileAccessService>.Instance;
        var service = new ConcordatoFacileAccessService(db, tenantContext, logger);
        var user = new ClaimsPrincipal();

        var result = await service.CheckAccessAsync(leaseId, user);

        Assert.False(result.HasAccess);
        Assert.Equal(CfAccessSource.None, result.Source);
        Assert.True(result.GiftAvailable);
    }

    [Fact]
    public async Task AdminAccess_DoesNotConsumeOrgGift()
    {
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        var availableGift = new ConcordatoFacileGift
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            GrantedBySubscriptionId = "sub_test_789",
            GrantedAt = DateTime.UtcNow,
            ConsumedAt = null,
            ConsumedForLeaseId = null,
            Status = ConcordatoFacileGiftStatus.Available
        };

        db.ConcordatoFacileGifts.Add(availableGift);
        await db.SaveChangesAsync();

        var tenantContext = new TestTenantContext(orgId);
        var logger = NullLogger<ConcordatoFacileAccessService>.Instance;
        var service = new ConcordatoFacileAccessService(db, tenantContext, logger);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("sub", "admin-user-456")
        });
        var adminUser = new ClaimsPrincipal(identity);

        var result = await service.CheckAccessAsync(leaseId, adminUser);
        await db.SaveChangesAsync();

        Assert.True(result.HasAccess);
        Assert.Equal(CfAccessSource.Admin, result.Source);

        var giftAfter = await db.ConcordatoFacileGifts.FindAsync(availableGift.Id);
        Assert.NotNull(giftAfter);
        Assert.Equal(ConcordatoFacileGiftStatus.Available, giftAfter.Status);
        Assert.Null(giftAfter.ConsumedForLeaseId);
        Assert.Null(giftAfter.ConsumedAt);

        var auditEvent = await db.LeaseEvents
            .FirstOrDefaultAsync(e => e.LeaseContractId == leaseId
                && e.EventType == LeaseEventType.ConcordatoFacileAdminAccess);
        Assert.NotNull(auditEvent);
        Assert.Contains("admin-user-456", auditEvent.Payload);
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"concordato-facile-access-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private sealed class TestTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;
        public bool FilterEnabled => true;
    }
}
