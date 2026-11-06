using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Runs one CRM sync in a dependency-injection scope of its own, under the tenant of the org it works for (E07/F01/US02/T03, ADR-022-R6, R17):
/// the way the event bus runs a handler. The scope has its own <c>DbContext</c>, so a booking that fails leaves nothing behind for the next one,
/// and the tenant query filter of the scope sees that org only, on top of the org every query of the sync names. For the callers that are not
/// in a request or an event: the recurring job and the demo seed.
/// </summary>
internal static class CrmSyncScopes
{
    /// <summary>Syncs the booker of a booking of the org (<see cref="ICrmProfileSyncService.SyncBookingAsync"/>) in a scope under the org.</summary>
    public static async Task<CrmSyncOutcome> SyncBookingAsync(
        IServiceScopeFactory scopeFactory,
        Guid orgId,
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(orgId);
        return await scope.ServiceProvider.GetRequiredService<ICrmProfileSyncService>().SyncBookingAsync(orgId, bookingId, cancellationToken);
    }

    /// <summary>Syncs a guest of the org (<see cref="ICrmProfileSyncService.SyncGuestAsync"/>) in a scope under the org.</summary>
    public static async Task<CrmSyncOutcome> SyncGuestAsync(
        IServiceScopeFactory scopeFactory,
        Guid orgId,
        Guid guestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(orgId);
        return await scope.ServiceProvider.GetRequiredService<ICrmProfileSyncService>().SyncGuestAsync(orgId, guestId, cancellationToken);
    }
}
