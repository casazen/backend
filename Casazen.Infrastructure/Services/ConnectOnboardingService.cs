using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stripe Connect onboarding of an org (Express account). Since BK-09 (A3-19) the linked account is replaced only when
/// Stripe says it does not exist or was revoked (<see cref="StripeConnectFailure.AccountUnavailable"/>): a rate limit, a
/// Stripe outage, a network error or a key problem throws and leaves the account, its capabilities and the payments
/// on it untouched. Creations of one org run one at a time and carry an idempotency key bound to the org.
/// </summary>
public class ConnectOnboardingService(
    AppDbContext dbContext,
    IStripeConnectGateway stripeConnectGateway,
    ILogger<ConnectOnboardingService> logger) : IConnectOnboardingService
{
    public async Task<ConnectStatus> GetStatusAsync(
        Guid orgId,
        bool refreshFromStripe,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw OrgNotFound(orgId);

        if (refreshFromStripe && !string.IsNullOrWhiteSpace(org.StripeConnectedAccountId))
        {
            try
            {
                var snapshot = await stripeConnectGateway.GetAccountAsync(org.StripeConnectedAccountId, cancellationToken);
                await PersistSnapshotAsync(org, snapshot, cancellationToken);
            }
            catch (StripeConnectException ex) when (ex.Failure == StripeConnectFailure.AccountUnavailable)
            {
                // A status read never unlinks: the id stays as the key of the replacement the onboarding creates
                // (EnsureExpressAccountAsync). Until then no checkout may use an account that Stripe no longer knows.
                logger.LogWarning(
                    "Stripe connected account {AccountId} of org {OrgId} is unavailable ({StripeErrorCode}): capabilities cleared until the onboarding replaces it",
                    org.StripeConnectedAccountId,
                    orgId,
                    ex.StripeErrorCode);
                await ClearCapabilitiesAsync(org, cancellationToken);
            }
        }

        return MapStatus(org);
    }

    public async Task<ConnectStatus> EnsureExpressAccountAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        // Held until commit or rollback, Stripe calls included: a parallel click waits, then reads the account created
        // here. Disposing without commit rolls back, so a failure never leaves a half-written org.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OrgConnectAccount, orgId.ToString("N")));

        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw OrgNotFound(orgId);
        // An instance tracked earlier in the request keeps the values read before the lock.
        await dbContext.Entry(org).ReloadAsync(cancellationToken);

        string? replacedAccountId = null;
        if (!string.IsNullOrWhiteSpace(org.StripeConnectedAccountId))
        {
            try
            {
                var snapshot = await stripeConnectGateway.GetAccountAsync(org.StripeConnectedAccountId, cancellationToken);
                await PersistSnapshotAsync(org, snapshot, cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                return MapStatus(org);
            }
            catch (StripeConnectException ex) when (ex.Failure == StripeConnectFailure.AccountUnavailable)
            {
                replacedAccountId = org.StripeConnectedAccountId;
                logger.LogWarning(
                    "Stripe connected account {AccountId} of org {OrgId} does not exist or was revoked ({StripeErrorCode}); creating a replacement",
                    replacedAccountId,
                    orgId,
                    ex.StripeErrorCode);
            }
        }

        var connectEmail = await ResolveConnectEmailAsync(org, cancellationToken);
        if (string.IsNullOrWhiteSpace(org.ContactEmail) && !string.IsNullOrWhiteSpace(connectEmail))
            org.ContactEmail = connectEmail;

        var accountId = await stripeConnectGateway.CreateExpressAccountAsync(
            connectEmail,
            AccountCreationIdempotencyKey(orgId, replacedAccountId),
            cancellationToken);

        org.StripeConnectedAccountId = accountId;
        org.ConnectChargesEnabled = false;
        org.ConnectPayoutsEnabled = false;
        org.ConnectDetailsSubmitted = false;
        org.ConnectRequirementsDueJson = null;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        if (replacedAccountId is null)
            logger.LogInformation("Created Stripe Connect account {AccountId} for org {OrgId}", accountId, orgId);
        else
        {
            logger.LogWarning(
                "Created Stripe Connect account {AccountId} for org {OrgId}, replacing unavailable account {ReplacedAccountId}",
                accountId,
                orgId,
                replacedAccountId);
        }

        try
        {
            var snapshot = await stripeConnectGateway.GetAccountAsync(accountId, cancellationToken);
            await PersistSnapshotAsync(org, snapshot, cancellationToken);
        }
        catch (StripeConnectException ex)
        {
            // The account exists and is linked: its capabilities arrive with account.updated or the next status read.
            logger.LogWarning(
                ex,
                "Stripe account {AccountId} created but refresh failed for org {OrgId} ({Failure})",
                accountId,
                orgId,
                ex.Failure);
        }

        return MapStatus(org);
    }

    public async Task<string> CreateOnboardingLinkAsync(
        Guid orgId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default)
    {
        var status = await EnsureExpressAccountAsync(orgId, cancellationToken);
        if (string.IsNullOrWhiteSpace(status.ConnectedAccountId))
            throw new InvalidOperationException("Connected account missing after ensure");

        return await stripeConnectGateway.CreateAccountOnboardingLinkAsync(
            status.ConnectedAccountId,
            returnUrl,
            refreshUrl,
            cancellationToken);
    }

    public async Task ApplyAccountUpdatedAsync(ConnectAccountSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(
            o => o.StripeConnectedAccountId == snapshot.AccountId,
            cancellationToken);

        if (org is null)
        {
            logger.LogWarning("account.updated for unknown connected account {AccountId}", snapshot.AccountId);
            return;
        }

        await PersistSnapshotAsync(org, snapshot, cancellationToken);
    }

    /// <summary>
    /// Stripe <c>Idempotency-Key</c> of the Express account creation of an org (BK-09, A3-19). A retry of a creation whose
    /// answer was lost returns the same account (Stripe keeps a key for at least 24 hours). The replacement of an
    /// unavailable account uses a key bound to that account: the key of the first creation would return the old one.
    /// </summary>
    internal static string AccountCreationIdempotencyKey(Guid orgId, string? replacedAccountId) =>
        replacedAccountId is null
            ? $"connect-account:{orgId:N}"
            : $"connect-account:{orgId:N}:replaces:{replacedAccountId}";

    private static async Task CommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// E-mail sent to Stripe for a new account: the org contact, else its oldest user. Stable across retries, since a
    /// different e-mail under the same idempotency key is refused by Stripe (<c>idempotency_error</c>).
    /// </summary>
    private async Task<string> ResolveConnectEmailAsync(Org org, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(org.ContactEmail))
            return org.ContactEmail.Trim();

        var userEmail = await dbContext.Users.AsNoTracking()
            .Where(u => u.OrgId == org.Id && u.Email != null && u.Email != string.Empty)
            .OrderBy(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);

        return userEmail?.Trim() ?? string.Empty;
    }

    private async Task PersistSnapshotAsync(Org org, ConnectAccountSnapshot snapshot, CancellationToken cancellationToken)
    {
        org.ConnectChargesEnabled = snapshot.ChargesEnabled;
        org.ConnectPayoutsEnabled = snapshot.PayoutsEnabled;
        org.ConnectDetailsSubmitted = snapshot.DetailsSubmitted;
        org.ConnectRequirementsDueJson = snapshot.RequirementsDue.Count == 0
            ? null
            : JsonSerializer.Serialize(snapshot.RequirementsDue);
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ClearCapabilitiesAsync(Org org, CancellationToken cancellationToken)
    {
        org.ConnectChargesEnabled = false;
        org.ConnectPayoutsEnabled = false;
        org.ConnectDetailsSubmitted = false;
        org.ConnectRequirementsDueJson = null;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static NotFoundException OrgNotFound(Guid orgId) =>
        new($"Org {orgId} not found") { Code = "org_not_found", MessageKey = "OrganizationNotFound" };

    private static ConnectStatus MapStatus(Org org)
    {
        IReadOnlyList<string> requirements = [];
        if (!string.IsNullOrWhiteSpace(org.ConnectRequirementsDueJson))
        {
            try
            {
                requirements = JsonSerializer.Deserialize<List<string>>(org.ConnectRequirementsDueJson) ?? [];
            }
            catch (JsonException)
            {
                requirements = [];
            }
        }

        return new ConnectStatus(
            org.StripeConnectedAccountId,
            org.ConnectChargesEnabled,
            org.ConnectPayoutsEnabled,
            org.ConnectDetailsSubmitted,
            requirements);
    }
}
