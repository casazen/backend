using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Proof tests for ConcordatoFacile payment webhook handling (ADR-005).
/// Verifies money rules (amount/currency validation) and data integrity (idempotency).
/// </summary>
public class ConcordatoFacilePaymentHandlerTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ConcordatoFacilePaymentHandlerTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// PROOF: Money rule - correct amount (11900 cents) and currency (EUR) grants access.
    /// ADR-005-R4, ADR-005-R5.
    /// </summary>
    [PostgresFact]
    public async Task PaymentIntentSucceeded_Amount11900EUR_GrantsAccess()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = new ConcordatoFacilePaymentHandler(db, NullLogger<ConcordatoFacilePaymentHandler>.Instance);

        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var sessionId = "cs_test_correct_amount";
        var intentId = "pi_test_correct_amount";

        // Seed org and lease
        db.Orgs.Add(new Org { Id = orgId, Name = "Test Org" });
        db.LeaseContracts.Add(new LeaseContract { Id = leaseId, OrgId = orgId });
        db.ConcordatoFacilePurchases.Add(new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = sessionId,
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Pending,
        });
        await db.SaveChangesAsync();

        var paymentIntent = new PaymentIntent
        {
            Id = intentId,
            Amount = 11900,
            Currency = "eur",
            Created = DateTime.UtcNow,
            InvoiceId = sessionId,
            Metadata = new Dictionary<string, string>
            {
                ["leaseId"] = leaseId.ToString(),
                ["orgId"] = orgId.ToString(),
            },
        };

        await handler.HandlePaymentIntentSucceededAsync(paymentIntent);

        var purchase = await db.ConcordatoFacilePurchases.FirstAsync(p => p.StripeCheckoutSessionId == sessionId);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, purchase.Status);
        Assert.NotNull(purchase.PaidAt);
        Assert.Equal(intentId, purchase.StripePaymentIntentId);
    }

    /// <summary>
    /// PROOF: Money rule - wrong amount or currency does NOT grant access.
    /// ADR-005-R4.
    /// </summary>
    [PostgresFact]
    public async Task PaymentIntentSucceeded_WrongAmount_DoesNotGrantAccess()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = new ConcordatoFacilePaymentHandler(db, NullLogger<ConcordatoFacilePaymentHandler>.Instance);

        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var sessionId = "cs_test_wrong_amount";

        // Seed org and lease
        db.Orgs.Add(new Org { Id = orgId, Name = "Test Org" });
        db.LeaseContracts.Add(new LeaseContract { Id = leaseId, OrgId = orgId });
        db.ConcordatoFacilePurchases.Add(new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = sessionId,
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Pending,
        });
        await db.SaveChangesAsync();

        // Wrong amount (12000 instead of 11900)
        var wrongAmountIntent = new PaymentIntent
        {
            Id = "pi_test_wrong_amount",
            Amount = 12000,
            Currency = "eur",
            Created = DateTime.UtcNow,
            InvoiceId = sessionId,
            Metadata = new Dictionary<string, string>
            {
                ["leaseId"] = leaseId.ToString(),
                ["orgId"] = orgId.ToString(),
            },
        };

        await handler.HandlePaymentIntentSucceededAsync(wrongAmountIntent);

        var purchase = await db.ConcordatoFacilePurchases.FirstAsync(p => p.StripeCheckoutSessionId == sessionId);
        // Access NOT granted: purchase status still Pending
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, purchase.Status);
        Assert.Null(purchase.PaidAt);

        // Also test wrong currency
        var wrongCurrencyIntent = new PaymentIntent
        {
            Id = "pi_test_wrong_currency",
            Amount = 11900,
            Currency = "usd",
            Created = DateTime.UtcNow,
            InvoiceId = sessionId,
            Metadata = new Dictionary<string, string>
            {
                ["leaseId"] = leaseId.ToString(),
                ["orgId"] = orgId.ToString(),
            },
        };

        await handler.HandlePaymentIntentSucceededAsync(wrongCurrencyIntent);

        purchase = await db.ConcordatoFacilePurchases.FirstAsync(p => p.StripeCheckoutSessionId == sessionId);
        // Still pending
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, purchase.Status);
        Assert.Null(purchase.PaidAt);
    }

    /// <summary>
    /// PROOF: Data integrity - duplicate event is idempotent, no double credit.
    /// ADR-005-R6.
    /// </summary>
    [PostgresFact]
    public async Task DuplicateEvent_IdempotentSuccess_NoDoubleCredit()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = new ConcordatoFacilePaymentHandler(db, NullLogger<ConcordatoFacilePaymentHandler>.Instance);

        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var sessionId = "cs_test_duplicate";
        var intentId = "pi_test_duplicate";

        // Seed org and lease
        db.Orgs.Add(new Org { Id = orgId, Name = "Test Org" });
        db.LeaseContracts.Add(new LeaseContract { Id = leaseId, OrgId = orgId });
        db.ConcordatoFacilePurchases.Add(new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = sessionId,
            AmountCents = 11900,
            Currency = "eur",
            Status = ConcordatoFacilePurchaseStatus.Pending,
        });
        await db.SaveChangesAsync();

        var paymentIntent = new PaymentIntent
        {
            Id = intentId,
            Amount = 11900,
            Currency = "eur",
            Created = DateTime.UtcNow,
            InvoiceId = sessionId,
            Metadata = new Dictionary<string, string>
            {
                ["leaseId"] = leaseId.ToString(),
                ["orgId"] = orgId.ToString(),
            },
        };

        // First processing
        await handler.HandlePaymentIntentSucceededAsync(paymentIntent);

        var purchase = await db.ConcordatoFacilePurchases.FirstAsync(p => p.StripeCheckoutSessionId == sessionId);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, purchase.Status);
        var firstPaidAt = purchase.PaidAt;
        Assert.NotNull(firstPaidAt);

        // Duplicate processing of the same event (same PaymentIntent)
        await handler.HandlePaymentIntentSucceededAsync(paymentIntent);

        purchase = await db.ConcordatoFacilePurchases.FirstAsync(p => p.StripeCheckoutSessionId == sessionId);
        // Status unchanged (still Completed, not duplicated)
        Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, purchase.Status);
        // PaidAt unchanged (idempotent)
        Assert.Equal(firstPaidAt, purchase.PaidAt);
        // No second purchase created
        Assert.Single(await db.ConcordatoFacilePurchases.Where(p => p.StripePaymentIntentId == intentId).ToListAsync());
    }
}
