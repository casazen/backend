using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe.Checkout;
using StripeConfiguration = Stripe.StripeConfiguration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Creates Stripe Checkout sessions for ConcordatoFacile one-time payments (ADR-005-R1, ADR-005-R2).
/// Reuses existing pending sessions to prevent duplicate payments.
/// </summary>
public sealed class ConcordatoFacileCheckoutService(
    AppDbContext dbContext,
    IOptions<ConcordatoFacileOptions> options,
    ILogger<ConcordatoFacileCheckoutService> logger) : IConcordatoFacileCheckoutService
{
    private const string PaymentMode = "payment";
    private const int AmountCents = 11900; // EUR 119,00
    private const string Currency = "eur";

    public async Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();

        // Check for existing pending purchase (ADR-005-R2)
        var existingPurchase = await dbContext.ConcordatoFacilePurchases
            .Where(p => p.OrgId == orgId
                && p.LeaseContractId == leaseId
                && p.Status == ConcordatoFacilePurchaseStatus.Pending)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingPurchase is not null)
        {
            // Check if session is still valid (not expired)
            var sessionService = new SessionService();
            try
            {
                var session = await sessionService.GetAsync(
                    existingPurchase.StripeCheckoutSessionId,
                    cancellationToken: cancellationToken);

                if (session.Status == "open")
                {
                    logger.LogInformation(
                        "Reusing existing checkout session {SessionId} for org {OrgId}, lease {LeaseId}",
                        session.Id,
                        orgId,
                        leaseId);

                    return new CheckoutResult(
                        session.Id,
                        session.Url ?? throw new InvalidOperationException("Stripe session URL missing"),
                        Reused: true);
                }
            }
            catch (Stripe.StripeException ex) when (ex.StripeError.Code == "resource_missing")
            {
                // Session no longer exists on Stripe, create a new one
                logger.LogInformation(
                    "Previous session {SessionId} not found on Stripe, creating new session",
                    existingPurchase.StripeCheckoutSessionId);
            }
        }

        // Create new checkout session (ADR-005-R1)
        var baseUrl = options.Value.BaseUrl ?? throw new InvalidOperationException("ConcordatoFacile:BaseUrl not configured");
        var priceId = options.Value.StripePriceId ?? throw new InvalidOperationException("ConcordatoFacile:StripePriceId not configured");

        var metadata = new Dictionary<string, string>
        {
            ["orgId"] = orgId.ToString(),
            ["leaseId"] = leaseId.ToString(),
            ["userId"] = userId
        };

        var createOptions = new SessionCreateOptions
        {
            Mode = PaymentMode,
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    Price = priceId,
                    Quantity = 1
                }
            },
            SuccessUrl = $"{baseUrl}/app/long-rent/leases/{leaseId}/concordatofacile?checkout=success",
            CancelUrl = $"{baseUrl}/app/long-rent/leases/{leaseId}/concordatofacile?checkout=cancelled",
            Metadata = metadata
        };

        var newSessionService = new SessionService();
        var newSession = await newSessionService.CreateAsync(createOptions, cancellationToken: cancellationToken);

        // Insert new purchase record
        var purchase = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = newSession.Id,
            AmountCents = AmountCents,
            Currency = Currency,
            Status = ConcordatoFacilePurchaseStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        dbContext.ConcordatoFacilePurchases.Add(purchase);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Created checkout session {SessionId} for org {OrgId}, lease {LeaseId}",
            newSession.Id,
            orgId,
            leaseId);

        return new CheckoutResult(
            newSession.Id,
            newSession.Url ?? throw new InvalidOperationException("Stripe checkout session URL missing"),
            Reused: false);
    }

    private void ConfigureStripeApiKey()
    {
        if (string.IsNullOrWhiteSpace(StripeConfiguration.ApiKey))
        {
            var apiKey = options.Value.StripeApiKey ?? Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                StripeConfiguration.ApiKey = apiKey;
            }
        }
    }
}
