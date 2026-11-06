using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingIntegrationService" />
/// <remarks>
/// <para><b>No token is ever read.</b> The connection is a projection of two columns (<c>Status</c>, <c>LastSyncedAt</c>); the entity is not
/// loaded, so the encrypted tokens never leave the database through this service (ADR-023-R13).</para>
/// <para><b>The same properties as the plan.</b> The senders and the channel states are counted over the properties
/// <see cref="OnboardingPlanService"/> counts (short-rent, active, in the org and in the reach of the scope), so the numbers of the settings and
/// the progress of the parts speak about the same houses. Every query names the org.</para>
/// <para><b>Mutation.</b> The only write is the one of <see cref="IOnboardingPlanService.ResumeAsync"/> (the deferral and its "To do"); no
/// sender, channel, mapping or connection is touched here.</para>
/// </remarks>
public sealed class OnboardingIntegrationService(
    AppDbContext db,
    IOnboardingPlanService plans,
    IHostEnvironment hostEnvironment,
    ILogger<OnboardingIntegrationService> logger) : IOnboardingIntegrationService
{
    public async Task<OnboardingIntegrationOverview> GetOverviewAsync(Guid orgId, HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.OrgId != orgId)
            throw new ArgumentException("The scope is not one of the org of the overview.", nameof(scope));

        var connection = await ConnectionAsync(orgId, ct);
        var plan = await plans.GetPlanAsync(orgId, scope, ct);
        var properties = await PropertiesAsync(orgId, scope, ct);

        return new OnboardingIntegrationOverview(
            connection,
            [.. plan.Parts.Select(part => new OnboardingIntegrationPart(part, part.State != OnboardingPartStates.NotAvailable))],
            await SenderCountsAsync(orgId, properties, ct),
            await ChannelCountsAsync(orgId, [.. properties.Select(property => property.Id)], ct));
    }

    public async Task<OnboardingPartReopen> ReopenPartAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("The plan belongs to an org.", nameof(orgId));
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var definition = OnboardingPartCatalog.Of(key);

        // The decision is the org's, so the state is the one over the whole org (every property), not the reach of the caller; the same read
        // the deferral makes before it raises a "To do".
        var plan = await plans.GetPlanAsync(orgId, new HostScope(orgId), ct);
        if (plan.Parts.Single(part => part.Key == key).State == OnboardingPartStates.NotAvailable)
            throw new DomainRuleException(OnboardingIntegrationCodes.PartNotReopenable, "OnboardingPartNotReopenable");

        // Clears the deferral and closes the "To do" (harmless on a part that was never put off); the wizard of the part changes the data.
        await plans.ResumeAsync(orgId, userId, key, ct);

        logger.LogInformation("Onboarding part {Part} of org {OrgId} reopened from the integration settings", definition.Code, orgId);
        return new OnboardingPartReopen(key, definition.Code, definition.LaunchTarget, OnboardingReopenFocus.Of(key));
    }

    /// <summary>
    /// The connection of the org in this environment. A missing connection has no status and needs no reconnection (it was never made); a
    /// disconnected one or one in error does.
    /// </summary>
    private async Task<OnboardingConnectionHealth> ConnectionAsync(Guid orgId, CancellationToken ct)
    {
        var environment = OctorateEnvironments.Of(hostEnvironment);
        var row = await db.OctorateConnections
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && c.Environment == environment)
            .Select(c => new { c.Status, c.LastSyncedAt })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? new OnboardingConnectionHealth(null, environment, null, false)
            : new OnboardingConnectionHealth(
                row.Status,
                environment,
                row.LastSyncedAt,
                row.Status is OctorateConnectionStatus.Disconnected or OctorateConnectionStatus.Error);
    }

    /// <summary>The short-rent, active properties of the org the scope reaches: the ones the plan counts, with their country.</summary>
    private async Task<IReadOnlyList<(Guid Id, string? Country)>> PropertiesAsync(Guid orgId, HostScope scope, CancellationToken ct)
    {
        var rows = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .InScope(scope)
            .Where(PropertyRentalModeRules.IsShortRent)
            .Where(p => p.IsActive)
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Country })
            .ToListAsync(ct);
        return [.. rows.Select(row => (row.Id, (string?)row.Country))];
    }

    /// <summary>
    /// The properties for each stored sender. A property outside Italy has no police sender to choose (the store says it does not apply), so it
    /// is in no count; a property with no stored choice is <c>notSet</c> (its default is not a choice).
    /// </summary>
    private async Task<OnboardingSenderCounts> SenderCountsAsync(
        Guid orgId,
        IReadOnlyList<(Guid Id, string? Country)> properties,
        CancellationToken ct)
    {
        var applicable = properties.Where(p => TouristTaxComune.IsItalianCountry(p.Country)).Select(p => p.Id).ToList();
        if (applicable.Count == 0)
            return new OnboardingSenderCounts(0, 0, 0, 0);

        var stored = await db.PropertyComplianceSenders
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && applicable.Contains(s.PropertyId))
            .GroupBy(s => s.AlloggiatiSender)
            .Select(group => new { Sender = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        int Of(ComplianceSenderType sender) => stored.Where(row => row.Sender == sender).Sum(row => row.Count);

        var channelManager = Of(ComplianceSenderType.ChannelManager);
        var casaZen = Of(ComplianceSenderType.CasaZen);
        var manual = Of(ComplianceSenderType.Manual);
        return new OnboardingSenderCounts(channelManager, casaZen, manual, applicable.Count - channelManager - casaZen - manual);
    }

    /// <summary>The channel states of the properties, for each publication status; every status is listed, with zero when none is in it.</summary>
    private async Task<IReadOnlyList<OnboardingChannelStatusCount>> ChannelCountsAsync(
        Guid orgId,
        IReadOnlyList<Guid> propertyIds,
        CancellationToken ct)
    {
        var ids = propertyIds.ToList();
        var counted = new Dictionary<ChannelPublishStatus, int>();
        if (ids.Count > 0)
        {
            var rows = await db.ListingChannelStates
                .AsNoTracking()
                .Where(s => s.OrgId == orgId && ids.Contains(s.PropertyId))
                .GroupBy(s => s.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() })
                .ToListAsync(ct);
            foreach (var row in rows)
                counted[row.Status] = row.Count;
        }

        return
        [
            .. Enum.GetValues<ChannelPublishStatus>()
                .OrderBy(status => (int)status)
                .Select(status => new OnboardingChannelStatusCount(status, counted.GetValueOrDefault(status))),
        ];
    }
}
