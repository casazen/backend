using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe.Checkout;
using StripeConfiguration = Stripe.StripeConfiguration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Creates Stripe Checkout sessions for ConcordatoFacile one-time payments (ADR-005-R1, ADR-005-R2).
/// Reuses the open session of a pending purchase and never creates a second session for a lease that has already paid (CF-FIX).
/// Requests go through <see cref="Stripe.IStripeClient"/>: the global client configured from the Stripe key, or the one passed
/// (the tests).
/// <para>
/// CF-BE: the checkouts of one lease run one at a time under a PostgreSQL advisory lock (<c>pg_advisory_xact_lock</c>, held across the Stripe
/// calls like the checkout of the plans), so two clicks on "pay" at once open one Session and write one pending purchase: the second request
/// finds the first and reuses it (CF-FIX-FU3). The Session is created with an explicit expiry
/// (<see cref="ConcordatoFacilePurchaseRules.CheckoutSessionLifetime"/>), after which its purchase counts as closed.
/// </para>
/// </summary>
public sealed class ConcordatoFacileCheckoutService(
    AppDbContext dbContext,
    IOptions<ConcordatoFacileOptions> options,
    ILogger<ConcordatoFacileCheckoutService> logger,
    Stripe.IStripeClient? stripeClient = null,
    TimeProvider? timeProvider = null) : IConcordatoFacileCheckoutService
{
    /// <summary>409 code of a lease that already has the service: paid, given as a gift, or paid and not yet recorded.</summary>
    public const string AlreadyPurchasedCode = "concordatofacile_already_purchased";

    private const string AlreadyPurchasedKey = "ConcordatoFacileAlreadyPurchased";
    private const string PaymentMode = "payment";
    private const int AmountCents = 11900; // EUR 119,00
    private const string Currency = "eur";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private Stripe.IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

    public async Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        // CF-FIX: only a lease of the caller's org can be paid for (404 lease_not_found otherwise, the rule shared with the status and the gift).
        await dbContext.EnsureLeaseOfOrgAsync(leaseId, orgId, cancellationToken);

        // CF-BE (CF-FIX-FU3): one checkout of the lease at a time. The lock is held until the commit, Stripe calls included: the request
        // that waits then sees the purchase (and the open Session) of the one before it and reuses it. Outside PostgreSQL there is no lock.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.ConcordatoFacileCheckout, leaseId.ToString("N")));

        CheckoutResult result;
        try
        {
            result = await CheckoutUnderLockAsync(leaseId, orgId, userId, cancellationToken);
        }
        catch (DomainConflictException)
        {
            // A 409 is an answer, not a failure: what was saved before it stays (a purchase completed from its paid Session, the
            // purchases closed on the way).
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            throw;
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return result;
    }

    private async Task<CheckoutResult> CheckoutUnderLockAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken)
    {
        // CF-FIX: a lease that already has the access (completed purchase or consumed gift) is never charged again, and Stripe is not called.
        if (await HasAccessAsync(leaseId, orgId, cancellationToken))
        {
            logger.LogInformation("Checkout refused: lease {LeaseId} of org {OrgId} already has the ConcordatoFacile access", leaseId, orgId);
            throw AlreadyPurchased();
        }

        if (stripeClient is null)
            ConfigureStripeApiKey();

        var sessions = new SessionService(Client);

        // ADR-005-R2 and CF-FIX: what became of the sessions of the pending purchases decides whether to reuse one, to refuse a
        // second payment (a session paid whose webhook has not arrived) or to open a new one.
        var reusable = await ResolvePendingPurchasesAsync(sessions, leaseId, orgId, cancellationToken);
        if (reusable is not null)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Reusing existing checkout session {SessionId} for org {OrgId}, lease {LeaseId}",
                reusable.SessionId,
                orgId,
                leaseId);
            return reusable;
        }

        // Create new checkout session (ADR-005-R1)
        var baseUrl = options.Value.BaseUrl ?? throw new InvalidOperationException("ConcordatoFacile:BaseUrl not configured");
        var priceId = options.Value.StripePriceId ?? throw new InvalidOperationException("ConcordatoFacile:StripePriceId not configured");

        // The PaymentIntent of a Checkout Session does not inherit the metadata of the session, and the webhook reads leaseId and
        // orgId from the PaymentIntent (ConcordatoFacilePaymentHandler): they are written on both. No "kind" key: it would send the
        // payment to another handler (StripeWebhookHandler.IsConcordatoFacilePayment recognizes a CF payment by its absence).
        //
        // CF-BE: the expiry is set here, not left to Stripe's default of 24 hours (it accepts 30 minutes to 24 hours): a purchase whose
        // Session has expired is a purchase nobody will pay, and ConcordatoFacilePurchaseRules closes it after this time and a margin.
        var now = _clock.GetUtcNow().UtcDateTime;
        var createOptions = new SessionCreateOptions
        {
            Mode = PaymentMode,
            ExpiresAt = now.Add(ConcordatoFacilePurchaseRules.CheckoutSessionLifetime),
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    Price = priceId,
                    Quantity = 1
                }
            },
            // CF-END: the page of the guided route (the old address, which the frontend keeps only as a redirect for the Sessions already
            // created, named the supplier in the address bar).
            SuccessUrl = $"{baseUrl}/app/long-rent/leases/{leaseId}/guided-contract?checkout=success",
            CancelUrl = $"{baseUrl}/app/long-rent/leases/{leaseId}/guided-contract?checkout=cancelled",
            Metadata = PaymentMetadata(orgId, leaseId, userId),
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = PaymentMetadata(orgId, leaseId, userId)
            }
        };

        var newSession = await sessions.CreateAsync(createOptions, cancellationToken: cancellationToken);

        // Insert new purchase record (the closed ones, if any, are saved with it)
        var purchase = new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = newSession.Id,
            AmountCents = AmountCents,
            Currency = Currency,
            Status = ConcordatoFacilePurchaseStatus.Pending,
            CreatedAt = now
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

    /// <summary>
    /// The access rule of <see cref="ConcordatoFacileAccessService"/> for a member of the org: a completed purchase of the lease or a
    /// gift consumed for it (the platform admin's access is not a payment and does not stop a checkout).
    /// </summary>
    private async Task<bool> HasAccessAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken)
    {
        var paid = await dbContext.ConcordatoFacilePurchases
            .AnyAsync(
                p => p.OrgId == orgId
                    && p.LeaseContractId == leaseId
                    && p.Status == ConcordatoFacilePurchaseStatus.Completed,
                cancellationToken);
        if (paid)
            return true;

        return await dbContext.ConcordatoFacileGifts
            .AnyAsync(g => g.OrgId == orgId && g.ConsumedForLeaseId == leaseId, cancellationToken);
    }

    /// <summary>
    /// Looks at the Stripe session of every pending purchase of the lease, newest first. An open session is returned to be reused;
    /// a session that is gone or expired closes its purchase (Failed, saved with the caller's next write); a completed session ends
    /// the checkout with the 409, because the customer has paid (or the payment is in flight) and must not pay again.
    /// </summary>
    private async Task<CheckoutResult?> ResolvePendingPurchasesAsync(
        SessionService sessions,
        Guid leaseId,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var pending = await dbContext.ConcordatoFacilePurchases
            .Where(p => p.OrgId == orgId
                && p.LeaseContractId == leaseId
                && p.Status == ConcordatoFacilePurchaseStatus.Pending)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        CheckoutResult? reusable = null;
        foreach (var purchase in pending)
        {
            var session = await FindSessionAsync(sessions, purchase, cancellationToken);
            switch (session?.Status)
            {
                case "open":
                    reusable ??= new CheckoutResult(
                        session.Id,
                        session.Url ?? throw new InvalidOperationException("Stripe session URL missing"),
                        Reused: true);
                    break;

                case "complete":
                    throw await RecordCompletedSessionAsync(purchase, session, cancellationToken);

                default:
                    // Expired, unknown, or no longer on Stripe: the purchase is closed and a new one takes its place.
                    purchase.Status = ConcordatoFacilePurchaseStatus.Failed;
                    break;
            }
        }

        return reusable;
    }

    private async Task<Session?> FindSessionAsync(
        SessionService sessions,
        ConcordatoFacilePurchase purchase,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sessions.GetAsync(purchase.StripeCheckoutSessionId, cancellationToken: cancellationToken);
        }
        catch (Stripe.StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            // Session no longer exists on Stripe, a new one will be created
            logger.LogInformation(
                "Previous session {SessionId} not found on Stripe, closing purchase {PurchaseId}",
                purchase.StripeCheckoutSessionId,
                purchase.Id);
            return null;
        }
    }

    /// <summary>
    /// A completed session: when it is paid for exactly the price of the service the purchase is completed now (the same transition
    /// as <see cref="ConcordatoFacilePaymentHandler.HandlePaymentIntentSucceededAsync"/>; the webhook that arrives later finds it
    /// completed and does nothing). A session completed but not yet paid is a payment in flight (delayed payment method), and a
    /// payment of another amount is an error for the operators: in both cases the customer is not charged a second time.
    /// </summary>
    private async Task<DomainConflictException> RecordCompletedSessionAsync(
        ConcordatoFacilePurchase purchase,
        Session session,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(session.PaymentStatus, "paid", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Checkout session {SessionId} of purchase {PurchaseId} is complete but not paid yet ({PaymentStatus}): no new session",
                session.Id,
                purchase.Id,
                session.PaymentStatus);
        }
        else if (session.AmountTotal != ConcordatoFacilePaymentHandler.PriceCents
            || !string.Equals(session.Currency, ConcordatoFacilePaymentHandler.Currency, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Checkout session {SessionId} of purchase {PurchaseId} was paid with the wrong amount ({Amount} {PaymentCurrency}, expected {PriceCents} {ExpectedCurrency}): access NOT granted, no new session",
                session.Id,
                purchase.Id,
                session.AmountTotal,
                session.Currency,
                ConcordatoFacilePaymentHandler.PriceCents,
                ConcordatoFacilePaymentHandler.Currency);
        }
        else
        {
            purchase.Status = ConcordatoFacilePurchaseStatus.Completed;
            purchase.PaidAt = DateTime.UtcNow;
            purchase.StripePaymentIntentId = session.PaymentIntentId;
            logger.LogWarning(
                "Checkout session {SessionId} is paid but the webhook has not arrived: purchase {PurchaseId} completed from the session (payment intent {PaymentIntentId})",
                session.Id,
                purchase.Id,
                session.PaymentIntentId);
        }

        // Also saves the purchases closed earlier in the same pass.
        await dbContext.SaveChangesAsync(cancellationToken);
        return AlreadyPurchased();
    }

    private static DomainConflictException AlreadyPurchased() => new(AlreadyPurchasedCode, AlreadyPurchasedKey);

    private static Dictionary<string, string> PaymentMetadata(Guid orgId, Guid leaseId, string userId) => new()
    {
        ["orgId"] = orgId.ToString(),
        ["leaseId"] = leaseId.ToString(),
        ["userId"] = userId
    };

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
