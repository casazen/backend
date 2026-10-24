using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// PROOF TESTS for ConcordatoFacileAccessService (ADR-005-R8).
/// These tests prove the money rule (access granted only after payment) and tenant isolation.
/// </summary>
public class ConcordatoFacileAccessServiceTests
{
    /// <summary>
    /// PROOF TEST: Money rule - access is granted ONLY when payment status is Completed.
    /// A pending payment must NOT grant access (ADR-005-R8).
    /// </summary>
    [Fact]
    public async Task CheckAccess_PaymentCompleted_ReturnsHasAccessTrue()
    {
        // Arrange
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        // Create a pending purchase (payment not completed yet)
        var pendingPurchase = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = "cs_test_pending",
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Pending, // NOT Completed
            CreatedAt = DateTime.UtcNow
        };

        db.ConcordatoFacilePurchases.Add(pendingPurchase);
        await db.SaveChangesAsync();

        var tenantContext = new TestTenantContext(orgId);
        var service = new ConcordatoFacileAccessService(db, tenantContext);
        var user = new ClaimsPrincipal();

        // Act: Check access with pending payment
        var resultPending = await service.CheckAccessAsync(leaseId, user);

        // Assert: Pending payment does NOT grant access (money rule)
        Assert.False(resultPending.HasAccess);
        Assert.Equal(CfAccessSource.None, resultPending.Source);

        // Now complete the payment
        pendingPurchase.Status = ConcordatoFacilePurchaseStatus.Completed;
        pendingPurchase.PaidAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Act: Check access with completed payment
        var resultCompleted = await service.CheckAccessAsync(leaseId, user);

        // Assert: Completed payment GRANTS access (money rule)
        Assert.True(resultCompleted.HasAccess);
        Assert.Equal(CfAccessSource.Payment, resultCompleted.Source);
        Assert.NotNull(resultCompleted.PurchasedAt);

        // PROOF: Access is granted ONLY when payment is completed, not before.
        // If the service ignored the status check, this test would fail.
    }

    /// <summary>
    /// PROOF TEST: Tenant isolation - a completed purchase for org A must NOT grant access
    /// to a user from org B (ADR-005-R8).
    /// </summary>
    [Fact]
    public async Task CheckAccess_OtherOrgPurchase_ReturnsHasAccessFalse()
    {
        // Arrange
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        // Org A has a completed purchase for the lease
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

        // Act: User from org B tries to check access
        var tenantContextOrgB = new TestTenantContext(orgB);
        var service = new ConcordatoFacileAccessService(db, tenantContextOrgB);
        var user = new ClaimsPrincipal();

        var result = await service.CheckAccessAsync(leaseId, user);

        // Assert: Org B user has NO access (tenant isolation)
        Assert.False(result.HasAccess);
        Assert.Equal(CfAccessSource.None, result.Source);

        // PROOF: The EF global query filter (ITenantOwned) prevents cross-org access.
        // If tenant isolation failed, org B would see org A's purchase and get access.
        // This test proves the service respects tenant boundaries (data rule).
    }

    /// <summary>
    /// Additional test: Admin role grants access regardless of payment or gift (ADR-005-R8, AC-NF04-1).
    /// </summary>
    [Fact]
    public async Task CheckAccess_AdminRole_ReturnsHasAccessTrue()
    {
        // Arrange
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();
        var tenantContext = new TestTenantContext(orgId);
        var service = new ConcordatoFacileAccessService(db, tenantContext);

        // Create a user with Admin role
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Admin")
        });
        var user = new ClaimsPrincipal(identity);

        // Act
        var result = await service.CheckAccessAsync(leaseId, user);

        // Assert: Admin has access without payment or gift
        Assert.True(result.HasAccess);
        Assert.Equal(CfAccessSource.Admin, result.Source);

        // PROOF: Admin role check takes precedence (permission rule).
    }

    /// <summary>
    /// Additional test: Gift consumed for the lease grants access (ADR-005-R8).
    /// </summary>
    [Fact]
    public async Task CheckAccess_GiftConsumed_ReturnsHasAccessTrue()
    {
        // Arrange
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        // Create a consumed gift for the lease
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
        var service = new ConcordatoFacileAccessService(db, tenantContext);
        var user = new ClaimsPrincipal();

        // Act
        var result = await service.CheckAccessAsync(leaseId, user);

        // Assert: Gift grants access
        Assert.True(result.HasAccess);
        Assert.Equal(CfAccessSource.Gift, result.Source);
        Assert.True(result.GiftConsumed);
        Assert.NotNull(result.GiftAssignedAt);
    }

    /// <summary>
    /// Additional test: Gift availability is included in the response (ADR-006 recommendation).
    /// </summary>
    [Fact]
    public async Task CheckAccess_AvailableGift_ReturnsGiftAvailableTrue()
    {
        // Arrange
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = NewDb();

        // Create an available (unconsumed) gift for the org
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
        var service = new ConcordatoFacileAccessService(db, tenantContext);
        var user = new ClaimsPrincipal();

        // Act
        var result = await service.CheckAccessAsync(leaseId, user);

        // Assert: No access yet, but gift is available
        Assert.False(result.HasAccess);
        Assert.Equal(CfAccessSource.None, result.Source);
        Assert.True(result.GiftAvailable);
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"concordato-facile-access-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Test implementation of ITenantContext for unit tests.
    /// </summary>
    private sealed class TestTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;
        public bool FilterEnabled => true;
    }
}
