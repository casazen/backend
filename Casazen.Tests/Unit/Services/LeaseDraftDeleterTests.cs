using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Tests for <see cref="LeaseDraftDeleter"/> CF draft protection (ADR-004): drafts with associated purchases must not be
/// automatically deleted.
/// </summary>
public class LeaseDraftDeleterTests
{
    private static AppDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static LeaseDraftDeleter CreateService(AppDbContext ctx) =>
        new LeaseDraftDeleter(
            ctx,
            new Mock<IFileStorage>().Object,
            new Mock<ILogger<LeaseDraftDeleter>>().Object);

    /// <summary>
    /// DATA RULE PROOF (ADR-004, AC-E01-F02-US01-02): a CF draft with a pending or completed purchase is not deletable, even
    /// if it has no parties, signed PDF or rent schedule. This protects the draft while the payment is active.
    /// </summary>
    [Fact]
    public async Task IsDeletableAsync_CfDraftWithPendingPurchase_ReturnsFalse()
    {
        using var ctx = CreateInMemoryContext(nameof(IsDeletableAsync_CfDraftWithPendingPurchase_ReturnsFalse));
        var deleter = CreateService(ctx);

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        // Arrange: minimal CF draft (deletable by default: no parties, no signed PDF, no rent schedule)
        var draft = new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            Status = LeaseStatus.Draft,
            IsConcordatoFacileSource = true,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2029, 1, 1),
            MonthlyRent = 0,
            WizardMaxStep = 1,
            DraftVersion = 1,
        };
        draft.SetContractTerms(LeaseContractType.Concordato, null);

        ctx.LeaseContracts.Add(draft);

        // Arrange: pending purchase for this draft
        var purchase = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = "cs_test_123",
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.ConcordatoFacilePurchases.Add(purchase);

        await ctx.SaveChangesAsync();

        // Act: check if the draft is deletable
        var isDeletable = await deleter.IsDeletableAsync(draft);

        // Assert: the draft must NOT be deletable because it has a pending purchase
        Assert.False(isDeletable, "CF draft with pending purchase must not be deletable");
    }

    /// <summary>
    /// DATA RULE PROOF (ADR-004, variant): a CF draft with a completed purchase is also protected.
    /// </summary>
    [Fact]
    public async Task IsDeletableAsync_CfDraftWithCompletedPurchase_ReturnsFalse()
    {
        using var ctx = CreateInMemoryContext(nameof(IsDeletableAsync_CfDraftWithCompletedPurchase_ReturnsFalse));
        var deleter = CreateService(ctx);

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        var draft = new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            Status = LeaseStatus.Draft,
            IsConcordatoFacileSource = true,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2029, 1, 1),
            MonthlyRent = 0,
            WizardMaxStep = 1,
            DraftVersion = 1,
        };
        draft.SetContractTerms(LeaseContractType.Concordato, null);

        ctx.LeaseContracts.Add(draft);

        var purchase = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = "cs_test_456",
            StripePaymentIntentId = "pi_test_456",
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            PaidAt = DateTime.UtcNow.AddHours(-1),
        };
        ctx.ConcordatoFacilePurchases.Add(purchase);

        await ctx.SaveChangesAsync();

        var isDeletable = await deleter.IsDeletableAsync(draft);

        Assert.False(isDeletable, "CF draft with completed purchase must not be deletable");
    }

    /// <summary>
    /// DATA RULE PROOF (ADR-004, variant): a CF draft with a failed purchase IS deletable (payment never succeeded).
    /// </summary>
    [Fact]
    public async Task IsDeletableAsync_CfDraftWithFailedPurchase_ReturnsTrue()
    {
        using var ctx = CreateInMemoryContext(nameof(IsDeletableAsync_CfDraftWithFailedPurchase_ReturnsTrue));
        var deleter = CreateService(ctx);

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        var draft = new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            Status = LeaseStatus.Draft,
            IsConcordatoFacileSource = true,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2029, 1, 1),
            MonthlyRent = 0,
            WizardMaxStep = 1,
            DraftVersion = 1,
        };
        draft.SetContractTerms(LeaseContractType.Concordato, null);

        ctx.LeaseContracts.Add(draft);

        var purchase = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = "cs_test_789",
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Failed,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        ctx.ConcordatoFacilePurchases.Add(purchase);

        await ctx.SaveChangesAsync();

        var isDeletable = await deleter.IsDeletableAsync(draft);

        Assert.True(isDeletable, "CF draft with failed purchase should be deletable");
    }

    /// <summary>
    /// DATA RULE PROOF (ADR-004, variant): a CF draft with no purchase is deletable (minimal draft before payment).
    /// </summary>
    [Fact]
    public async Task IsDeletableAsync_CfDraftWithNoPurchase_ReturnsTrue()
    {
        using var ctx = CreateInMemoryContext(nameof(IsDeletableAsync_CfDraftWithNoPurchase_ReturnsTrue));
        var deleter = CreateService(ctx);

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        var draft = new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            Status = LeaseStatus.Draft,
            IsConcordatoFacileSource = true,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2029, 1, 1),
            MonthlyRent = 0,
            WizardMaxStep = 1,
            DraftVersion = 1,
        };
        draft.SetContractTerms(LeaseContractType.Concordato, null);

        ctx.LeaseContracts.Add(draft);
        await ctx.SaveChangesAsync();

        var isDeletable = await deleter.IsDeletableAsync(draft);

        Assert.True(isDeletable, "CF draft with no purchase should be deletable");
    }
}
