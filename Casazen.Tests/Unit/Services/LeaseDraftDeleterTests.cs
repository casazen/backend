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

    [Fact]
    public async Task IsDeletableAsync_CfDraftWithPendingPurchase_ReturnsFalse()
    {
        using var ctx = CreateInMemoryContext(nameof(IsDeletableAsync_CfDraftWithPendingPurchase_ReturnsFalse));
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
            StripeCheckoutSessionId = "cs_test_123",
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.ConcordatoFacilePurchases.Add(purchase);

        await ctx.SaveChangesAsync();

        var isDeletable = await deleter.IsDeletableAsync(draft);

        // Assert: the draft must NOT be deletable because it has a pending purchase
        Assert.False(isDeletable, "CF draft with pending purchase must not be deletable");
    }

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
