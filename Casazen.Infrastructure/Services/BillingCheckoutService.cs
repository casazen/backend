using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stripe Checkout of a paid plan with at most one subscription per org (A1-10). Checkouts of one org run one at a
/// time under a PostgreSQL advisory lock (double click, two tabs, two API instances). Before creating a session the
/// org's subscriptions are read from Stripe too: a checkout paid a moment ago in another tab may not have reached
/// the webhook yet. An open session for the same tier is reused; open sessions for another tier are expired.
/// </summary>
public sealed class BillingCheckoutService(
    AppDbContext dbContext,
    IStripeBillingService stripeBillingService,
    ILogger<BillingCheckoutService> logger) : IBillingCheckoutService
{
    public async Task<string> StartCheckoutAsync(
        Guid orgId,
        PlanTier planTier,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        // The lock is held until commit or rollback, Stripe calls included: the next checkout of the org waits and
        // then sees the customer id and the session created here. Disposing without commit rolls back.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OrgBillingCheckout, orgId.ToString("N")));

        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new NotFoundException($"Org {orgId} not found") { MessageKey = "OrganizationNotFound" };
        // An instance tracked earlier in the request keeps the values read before the lock: read what the previous
        // holder committed (customer id, subscription stored by the webhook).
        await dbContext.Entry(org).ReloadAsync(cancellationToken);

        if (BillingSubscriptionPolicy.BlocksNewCheckout(org))
        {
            logger.LogInformation(
                "Checkout refused for org {OrgId}: subscription {SubscriptionId} is {SubscriptionStatus}",
                org.Id,
                org.SubscriptionId,
                org.SubscriptionStatus);
            throw AlreadySubscribed();
        }

        StripeCheckoutSession? reusable = null;
        if (string.IsNullOrWhiteSpace(org.StripeCustomerId))
        {
            org.StripeCustomerId = await stripeBillingService.EnsureCustomerAsync(org, cancellationToken);
            org.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await EnsureNoSubscriptionOnStripeAsync(org, cancellationToken);
            reusable = await SettleOpenSessionsAsync(org, planTier, cancellationToken);
        }

        var session = reusable ?? await stripeBillingService.CreateCheckoutSessionAsync(
            org,
            planTier,
            successUrl,
            cancelUrl,
            cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Checkout session {SessionId} for org {OrgId}, tier {PlanTier} ({Outcome})",
            session.Id,
            org.Id,
            planTier,
            reusable is null ? "created" : "reused");
        return session.Url;
    }

    private async Task EnsureNoSubscriptionOnStripeAsync(Org org, CancellationToken cancellationToken)
    {
        var subscriptions = await stripeBillingService.ListSubscriptionsAsync(org.StripeCustomerId!, cancellationToken);
        var live = subscriptions.FirstOrDefault(s => BillingSubscriptionPolicy.BlocksNewCheckout(s.Status));
        if (live is null)
            return;

        logger.LogWarning(
            "Checkout refused for org {OrgId}: Stripe subscription {SubscriptionId} is {StripeStatus} but the org stores {SubscriptionStatus} (webhook pending?)",
            org.Id,
            live.Id,
            live.Status,
            org.SubscriptionStatus);
        throw AlreadySubscribed();
    }

    /// <summary>
    /// Returns an open session for <paramref name="planTier"/> to reuse, and expires every other open session of the
    /// customer so that at most one checkout can be completed.
    /// </summary>
    private async Task<StripeCheckoutSession?> SettleOpenSessionsAsync(Org org, PlanTier planTier, CancellationToken cancellationToken)
    {
        var open = await stripeBillingService.ListOpenCheckoutSessionsAsync(org.StripeCustomerId!, cancellationToken);
        var reusable = open.FirstOrDefault(s => s.PlanTier == planTier);

        foreach (var session in open.Where(s => !ReferenceEquals(s, reusable)))
        {
            await stripeBillingService.ExpireCheckoutSessionAsync(session.Id, cancellationToken);
            logger.LogInformation("Expired checkout session {SessionId} of org {OrgId}", session.Id, org.Id);
        }

        return reusable;
    }

    private static DomainConflictException AlreadySubscribed() =>
        new(BillingSubscriptionPolicy.AlreadySubscribedCode, BillingSubscriptionPolicy.AlreadySubscribedMessageKey);
}
