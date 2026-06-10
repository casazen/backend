using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

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
            ?? throw new InvalidOperationException($"Org {orgId} not found");

        if (refreshFromStripe && !string.IsNullOrWhiteSpace(org.StripeConnectedAccountId))
        {
            var snapshot = await stripeConnectGateway.GetAccountAsync(org.StripeConnectedAccountId, cancellationToken);
            await PersistSnapshotAsync(org, snapshot, cancellationToken);
        }

        return MapStatus(org);
    }

    public async Task<ConnectStatus> EnsureExpressAccountAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new InvalidOperationException($"Org {orgId} not found");

        if (!string.IsNullOrWhiteSpace(org.StripeConnectedAccountId))
            return await GetStatusAsync(orgId, refreshFromStripe: true, cancellationToken);

        var accountId = await stripeConnectGateway.CreateExpressAccountAsync(org.ContactEmail, cancellationToken);
        org.StripeConnectedAccountId = accountId;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created Stripe Connect account {AccountId} for org {OrgId}", accountId, orgId);
        return await GetStatusAsync(orgId, refreshFromStripe: true, cancellationToken);
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
