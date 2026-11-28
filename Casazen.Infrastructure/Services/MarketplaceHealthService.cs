using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplaceHealthService"/> (E09/F03/US01/T01, NF-57, MKT-04, OCT-14, D22, D30, S25): the health read behind the Active tab and the sync that keeps it
/// true. The read is CasaZen's own rows and the quota of the last sync; the sync is the only caller of Octorate, and it only reads (<c>GET /rest/v1/connection</c>,
/// the license, the configuration). The rows are written through the same entity the activation wizard writes, so a status the wizard set is read back, and a status
/// the sync sets is what the card shows.
/// </summary>
/// <remarks>
/// <para><b>One row, one fact.</b> A connection that Octorate lists <c>ENABLED</c> keeps an Active activation Active and makes any other Active (verified) only when its
/// pulls are on too (<see cref="OctorateConnectionInfo.PullsAreOn"/>: the wizard switches them on as the last step of the activation, so «enabled» alone is an access
/// given, not a finished activation; with the pulls off a Pending row stays Pending and a row to fix keeps its problem and its «To do»), <c>WAITING</c> makes it Pending,
/// <c>DISABLED</c> To fix with <c>connection_disabled</c>. A connection the activation has an id for that Octorate no longer lists is To fix with <c>connection_missing</c>, unless the
/// row was written in the last <see cref="NewConnectionGrace"/> (the list may lag just after a create): that is left for the next run. When the list shows none of the
/// connections of the property (empty, or all gone at once) the first pass concludes nothing and a second one confirms it (<see cref="MissConfirmationWindow"/>); an answer
/// of Octorate that is not a list of connections is a failure of the read, never an empty list (the client refuses it). A status nobody knew changes
/// nothing, not even <c>LastSyncedAt</c>, and is logged as a warning. A row with no connection id (a start whose answer was lost, or one a step freed) has nothing to
/// compare and is left to the wizard, which finds or makes the connection when it starts again.</para>
/// <para><b>The «To do».</b> An activation that is To fix and has no item gets one (idempotent: an item that is open for it is the one, so there is never a second);
/// the id is kept in <c>TodoActivityId</c>. When the activation is no longer To fix, the item is closed by the system and the id cleared, so the next problem raises
/// a new one. An item the host closed by hand while the problem stays is not raised again: the id stays until the problem is gone. The urgency is High for a channel
/// (bookings stop arriving) and Medium for a tool or a function; the reason is the code of the problem, never a name or a person (ADR-026-R3).</para>
/// <para><b>Stops cleanly.</b> Every property is saved before the next one is read. The first <c>OctorateRateLimitException</c> ends the run after the «To do» items of
/// that property are brought in line with what is known: the rest is left to the next run, which reads the least recently synced first. Any other failure of
/// Octorate for a property is logged (the id of the property and the code, never a message or a body) and counted, and the next property goes on; a refusal of the org's own
/// token ends the org's run, because its other properties would fail alike.</para>
/// <para><b>Logs.</b> Ids, counts and codes: never a name, a credential, the text of a channel or the body of an answer of Octorate.</para>
/// </remarks>
public sealed class MarketplaceHealthService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IInternalActivityService activities,
    IMarketplaceLicensedFunctionService licensedFunctions,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    IMarketplaceTexts texts,
    IMemoryCache cache,
    IOptions<MarketplaceHealthOptions> options,
    TimeProvider timeProvider,
    ILogger<MarketplaceHealthService> logger) : IMarketplaceHealthService
{
    public const string TodoEntityType = nameof(MarketplaceActivation);

    /// <summary>
    /// A connection missing from the list is not taken as gone for a row written this recently: the list of Octorate may lag just after a create, and the sync must not
    /// raise a problem for a connection that was made a minute ago.
    /// </summary>
    public static readonly TimeSpan NewConnectionGrace = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan MissConfirmationWindow = TimeSpan.FromHours(3);

    private static readonly TimeSpan QuotaLifetime = TimeSpan.FromHours(3);

    private static readonly MarketplaceActivationKind[] HealthKinds =
    [
        MarketplaceActivationKind.Channel,
        MarketplaceActivationKind.Tool,
        MarketplaceActivationKind.LicensedFunction,
    ];

    public async Task<MarketplaceHealth> GetHealthAsync(
        Guid orgId,
        HostScope scope,
        Guid? propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var lang = MarketplaceLanguages.Normalize(language);

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return new MarketplaceHealth([]);

        await MarketplaceReads.EnsurePropertyInScopeAsync(db, orgId, scope, propertyId, ct);
        var rows = await ReachableRows(orgId, scope, propertyId).ToListAsync(ct);

        var propertyIds = rows.Where(r => r.PropertyId is not null).Select(r => r.PropertyId!.Value).Distinct().ToList();
        var propertyNames = propertyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Properties.AsNoTracking()
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var portalCodes = rows.Where(r => r.Kind != MarketplaceActivationKind.LicensedFunction).Select(r => r.TargetCode).Distinct().ToList();
        var portalNames = portalCodes.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await db.MarketplacePortals.AsNoTracking()
                .Where(p => portalCodes.Contains(p.PortalCode))
                .Select(p => new { p.PortalCode, p.Name })
                .ToDictionaryAsync(p => p.PortalCode, p => p.Name, StringComparer.Ordinal, ct);

        var quota = QuotaOf(orgId);
        var items = rows
            .Select(row => new MarketplaceHealthItem(
                row.Id,
                row.PropertyId,
                row.PropertyId is { } id && propertyNames.TryGetValue(id, out var propertyName) ? propertyName : null,
                row.Kind,
                row.TargetCode,
                NameOf(row, portalNames, lang),
                row.Status,
                row.IsDeclared,
                row.LastSyncedAt,
                ErrorOf(row.LastErrorCode, lang),
                quota,
                row.ChangeRequestedAt))
            .OrderBy(item => item.PropertyName is null)
            .ThenBy(item => item.PropertyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ActivationId)
            .ToList();
        return new MarketplaceHealth(items);
    }

    private IQueryable<MarketplaceActivation> ReachableRows(Guid orgId, HostScope scope, Guid? propertyId)
    {
        var rows = db.MarketplaceActivations.AsNoTracking()
            .Where(a => a.OrgId == orgId && HealthKinds.Contains(a.Kind) && a.Status != MarketplaceActivationStatus.Deactivated);

        if (propertyId is { } id)
            return rows.Where(a => a.PropertyId == id);

        var reachable = db.Properties.Where(p => p.OrgId == orgId).InScope(scope).Select(p => p.Id);
        var wholeOrg = scope.IsOrgWide;
        return rows.Where(a => (wholeOrg && a.PropertyId == null) || (a.PropertyId != null && reachable.Contains(a.PropertyId.Value)));
    }

    private string NameOf(MarketplaceActivation row, IReadOnlyDictionary<string, string> portalNames, string lang)
    {
        if (row.Kind == MarketplaceActivationKind.LicensedFunction)
            return texts.Get($"Marketplace.Licensed.{row.TargetCode}.Name", lang);

        return portalNames.TryGetValue(row.TargetCode, out var name) && !string.IsNullOrWhiteSpace(name) ? name : row.TargetCode;
    }

    private MarketplaceHealthError? ErrorOf(string? code, string lang)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var key = MarketplaceHealthErrorCodes.MessageKeyOf(code);
        var message = texts.Get(key, lang);
        if (string.Equals(message, key, StringComparison.Ordinal))
            message = texts.Get(MarketplaceHealthErrorCodes.GenericMessageKey, lang);

        return new MarketplaceHealthError(code, message);
    }

    private MarketplaceQuota? QuotaOf(Guid orgId) =>
        cache.TryGetValue(QuotaKey(orgId), out MarketplaceQuota? quota) ? quota : null;

    private static string QuotaKey(Guid orgId) => $"marketplace-quota:{orgId:N}";

    public async Task<IReadOnlyList<Guid>> ListOrgsToSyncAsync(CancellationToken ct = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return [];

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var connected = db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => c.OrgId);

        var rows = await db.MarketplaceActivations
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(a => connected.Contains(a.OrgId)
                        && HealthKinds.Contains(a.Kind)
                        && a.Status != MarketplaceActivationStatus.Deactivated
                        && a.PropertyId != null)
            .Select(a => new { a.OrgId, a.LastSyncedAt })
            .ToListAsync(ct);

        return
        [
            .. rows
                .GroupBy(row => row.OrgId)
                .OrderBy(group => group.Min(row => row.LastSyncedAt ?? DateTime.MinValue))
                .ThenBy(group => group.Key)
                .Select(group => group.Key),
        ];
    }

    public async Task<MarketplaceSyncResult> SyncAsync(Guid orgId, CancellationToken ct = default)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("A sync is for an organization.", nameof(orgId));

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector) || await ConnectedAccountAsync(orgId, ct) is not { } connectionKey)
            return MarketplaceSyncResult.Empty;

        var run = new RunCounts();
        await ReadQuotaAsync(orgId, run, ct);

        foreach (var plan in await PlanAsync(orgId, connectionKey, ct))
        {
            ct.ThrowIfCancellationRequested();

            bool goOn;
            try
            {
                goOn = await SyncPropertyAsync(orgId, plan, run, ct);
            }
            catch (OctorateRateLimitException)
            {
                await ReconcileTodosAsync(orgId, plan.PropertyId, run, ct);
                logger.LogWarning(
                    "Marketplace sync of org {OrgId} stopped by the rate limit at property {PropertyId}: {Properties} properties read, the rest is left to the next run",
                    orgId, plan.PropertyId, run.Properties);
                throw;
            }

            await ReconcileTodosAsync(orgId, plan.PropertyId, run, ct);
            if (!goOn)
                break;
        }

        var result = run.ToResult();
        logger.LogInformation(
            "Marketplace sync of org {OrgId}: {Properties} properties read, {Updated} activations changed, {Unrecognised} unrecognised statuses, {Skipped} skipped, {Failed} failed, {TodoRaised} To do raised, {TodoClosed} closed",
            orgId, result.Properties, result.Updated, result.Unrecognised, result.Skipped, result.Failed, result.TodoRaised, result.TodoClosed);
        return result;
    }

    private async Task<List<PropertyPlan>> PlanAsync(Guid orgId, Guid connectionKey, CancellationToken ct)
    {
        var existing = db.Properties.Where(p => p.OrgId == orgId).Select(p => p.Id);
        var facts = await db.MarketplaceActivations.AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && HealthKinds.Contains(a.Kind)
                        && a.Status != MarketplaceActivationStatus.Deactivated
                        && a.PropertyId != null
                        && existing.Contains(a.PropertyId.Value))
            .Select(a => new { a.PropertyId, a.Kind, a.LastSyncedAt })
            .ToListAsync(ct);
        if (facts.Count == 0)
            return [];

        var listings = await db.OctorateListingMappings.AsNoTracking()
            .Where(m => m.OrgId == orgId
                        && m.ConnectionId == connectionKey
                        && m.Status == OctorateMappingStatus.Confirmed
                        && m.PropertyId != null)
            .Select(m => new { m.PropertyId, m.OctorateAccommodationId })
            .ToListAsync(ct);
        var accommodationOf = listings
            .GroupBy(m => m.PropertyId!.Value)
            .ToDictionary(group => group.Key, group => group.First().OctorateAccommodationId);

        return
        [
            .. facts
                .GroupBy(fact => fact.PropertyId!.Value)
                .Select(group => new
                {
                    PropertyId = group.Key,
                    Oldest = group.Min(fact => fact.LastSyncedAt ?? DateTime.MinValue),
                    HasConnections = group.Any(fact => fact.Kind != MarketplaceActivationKind.LicensedFunction),
                    HasFunctions = group.Any(fact => fact.Kind == MarketplaceActivationKind.LicensedFunction),
                })
                .OrderBy(plan => plan.Oldest)
                .ThenBy(plan => plan.PropertyId)
                .Select(plan => new PropertyPlan(
                    plan.PropertyId,
                    accommodationOf.GetValueOrDefault(plan.PropertyId),
                    plan.HasConnections,
                    plan.HasFunctions)),
        ];
    }

    private async Task<bool> SyncPropertyAsync(Guid orgId, PropertyPlan plan, RunCounts run, CancellationToken ct)
    {
        if (plan.AccommodationId is not { } accommodationId)
        {
            run.Skipped++;
            logger.LogInformation(
                "Marketplace sync of org {OrgId}: property {PropertyId} has no confirmed listing, its activations are left as they are", orgId, plan.PropertyId);
            return true;
        }

        try
        {
            if (plan.HasConnections)
                await SyncConnectionsAsync(orgId, plan.PropertyId, accommodationId, run, ct);

            if (plan.HasFunctions)
                await ReconcileLicenseAsync(orgId, plan.PropertyId, run, ct);

            return true;
        }
        catch (OctorateException ex) when (ex is OctorateAuthException or OctorateWrongEnvironmentException)
        {
            run.Failed++;
            logger.LogWarning(
                "Marketplace sync of org {OrgId} stopped at property {PropertyId} ({Error}): its other properties are left for the next run",
                orgId, plan.PropertyId, ex.Code);
            return false;
        }
        catch (OctorateException ex) when (ex is not OctorateRateLimitException)
        {
            run.Failed++;
            logger.LogWarning(
                "Marketplace sync of property {PropertyId} of org {OrgId} failed ({Error}, HTTP {StatusCode}): the next run reads it again",
                plan.PropertyId, orgId, ex.Code, ex.OctorateStatusCode);
            return true;
        }
    }

    private async Task SyncConnectionsAsync(Guid orgId, Guid propertyId, string accommodationId, RunCounts run, CancellationToken ct)
    {
        var rows = await db.MarketplaceActivations
            .Where(a => a.OrgId == orgId
                        && a.PropertyId == propertyId
                        && (a.Kind == MarketplaceActivationKind.Channel || a.Kind == MarketplaceActivationKind.Tool)
                        && a.Status != MarketplaceActivationStatus.Deactivated
                        && a.OctorateConnectionId != null)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return;

        OctorateConnectionInfo[] listed;
        using (orgScope.Use(orgId))
            listed = await client.ListConnectionsAsync(accommodationId, ct);

        run.Properties++;
        var now = Now();
        var matches = rows
            .Select(row => (
                Row: row,
                Connection: listed.FirstOrDefault(
                    c => string.Equals(c.ConnectionId, row.OctorateConnectionId, StringComparison.Ordinal)
                         && string.Equals(c.PortalCode, row.TargetCode, StringComparison.Ordinal))))
            .ToList();

        var noneListed = matches.All(match => match.Connection is null);
        var updated = 0;
        foreach (var (row, connection) in matches)
        {
            if (Apply(row, connection, noneListed, now, run))
                updated++;
        }

        if (noneListed && updated == 0 && matches.Any(match => now - match.Row.UpdatedAt >= NewConnectionGrace))
        {
            run.Skipped++;
            logger.LogWarning(
                "Marketplace sync of property {PropertyId} of org {OrgId}: the list shows none of the {Count} connections of CasaZen ({Error}), nothing is concluded from one pass: a second pass that finds them gone confirms it",
                propertyId, orgId, rows.Count, MarketplaceHealthErrorCodes.ConnectionMissing);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            run.Updated += updated;
        }
        catch (DbUpdateConcurrencyException)
        {
            ForgetTrackedRows();
            run.Skipped++;
            logger.LogInformation(
                "Marketplace sync of property {PropertyId} of org {OrgId}: the activations were changed meanwhile, left as they are", propertyId, orgId);
        }
    }

    private bool Apply(MarketplaceActivation row, OctorateConnectionInfo? connection, bool noneListed, DateTime now, RunCounts run)
    {
        if (connection is null)
            return ApplyMissing(row, noneListed, now);

        var verdict = MarketplaceHealthRules.Classify(connection.Status);
        switch (verdict.Health)
        {
            case MarketplaceConnectionHealth.Healthy:
                return ApplyEnabled(row, connection, now);

            case MarketplaceConnectionHealth.Waiting:
                return MarkHealthy(row, MarketplaceActivationStatus.Pending, now);

            case MarketplaceConnectionHealth.Broken:
                return MarkToFix(row, verdict.ErrorCode!, now);

            default:
                ForgetMiss(row);
                run.Unrecognised++;
                logger.LogWarning(
                    "Marketplace activation {ActivationId} of org {OrgId}: the connection has a status CasaZen does not know ({Status}), left as it is",
                    row.Id, row.OrgId, Printable(connection.Status));
                return false;
        }
    }

    /// <summary>
    /// A connection the activation has an id for that the list does not show. A row written in the last <see cref="NewConnectionGrace"/> is left for the next run (the list may
    /// lag just after a create). When the list shows other connections of CasaZen for the property, it is readable and this one is gone: To fix with <c>connection_missing</c>
    /// at once. When it shows none of them (<paramref name="noneListed"/>: an empty list, or every connection of the property gone together) nothing is concluded from one
    /// pass: the first only remembers it (the moment in <c>LastErrorAt</c>, which a row with no problem leaves empty and the next sight of its connection clears with the
    /// rest of the problem, so no error is shown and no «To do» is raised), and a pass that finds them still gone, a run later and within
    /// <see cref="MissConfirmationWindow"/>, confirms it. A row that is already to fix, or has a problem of its own, keeps it.
    /// </summary>
    private bool ApplyMissing(MarketplaceActivation row, bool noneListed, DateTime now)
    {
        if (now - row.UpdatedAt < NewConnectionGrace)
        {
            logger.LogInformation(
                "Marketplace activation {ActivationId} of org {OrgId}: its connection is not listed yet, written {Minutes} minutes ago: left for the next run",
                row.Id, row.OrgId, (int)(now - row.UpdatedAt).TotalMinutes);
            return false;
        }

        if (!noneListed)
            return MarkToFix(row, MarketplaceHealthErrorCodes.ConnectionMissing, now);

        if (row.Status == MarketplaceActivationStatus.ToFix || row.LastErrorCode is not null)
            return false;

        if (row.LastErrorAt is { } firstMiss && now - firstMiss >= NewConnectionGrace && now - firstMiss <= MissConfirmationWindow)
            return MarkToFix(row, MarketplaceHealthErrorCodes.ConnectionMissing, now);

        if (row.LastErrorAt is null || now - row.LastErrorAt.Value > MissConfirmationWindow)
            row.LastErrorAt = now;

        return false;
    }

    /// <summary>
    /// A connection Octorate lists <c>ENABLED</c> shows the access given, not the activation finished: the activation wizard writes Active only after «mark connected»
    /// and once the pulls of the connection are on (D6, fail closed), and an Active row whose pulls are on is all it leaves alone. So an Active row stays Active, and a row
    /// that is not Active becomes Active only when the connection is enabled <b>and</b> its pulls are on (<see cref="OctorateConnectionInfo.PullsAreOn"/>); an enabled
    /// connection whose pulls are still off leaves the row as it is (a Pending row stays Pending, a row to fix keeps its problem and its open «To do») and only the
    /// moment of the read is kept: finishing the activation, and switching the pulls on, is the wizard's.
    /// </summary>
    private static bool ApplyEnabled(MarketplaceActivation row, OctorateConnectionInfo connection, DateTime now)
    {
        if (row.Status == MarketplaceActivationStatus.Active || connection.PullsAreOn)
            return MarkHealthy(row, MarketplaceActivationStatus.Active, now);

        ForgetMiss(row);
        row.LastSyncedAt = now;
        return false;
    }

    private static void ForgetMiss(MarketplaceActivation row)
    {
        if (row.LastErrorCode is null)
            row.LastErrorAt = null;
    }

    private static bool MarkHealthy(MarketplaceActivation row, MarketplaceActivationStatus status, DateTime now)
    {
        var changed = row.Status != status || row.LastErrorCode is not null;
        row.Status = status;
        row.LastErrorCode = null;
        row.LastErrorAt = null;
        row.LastSyncedAt = now;
        if (status == MarketplaceActivationStatus.Active)
        {
            row.IsDeclared = false;
            row.VerifiedAt ??= now;
        }

        if (changed)
            row.UpdatedAt = now;

        return changed;
    }

    private static bool MarkToFix(MarketplaceActivation row, string code, DateTime now)
    {
        var changed = row.Status != MarketplaceActivationStatus.ToFix || !string.Equals(row.LastErrorCode, code, StringComparison.Ordinal);
        if (changed)
        {
            row.Status = MarketplaceActivationStatus.ToFix;
            row.LastErrorCode = code;
            row.LastErrorAt = now;
            row.UpdatedAt = now;
        }

        row.LastSyncedAt = now;
        return changed;
    }

    private async Task ReconcileLicenseAsync(Guid orgId, Guid propertyId, RunCounts run, CancellationToken ct)
    {
        try
        {
            await licensedFunctions.ReconcileAsync(orgId, propertyId, ct);
        }
        catch (Exception ex) when (ex is DomainException or NotFoundException)
        {
            run.Skipped++;
            var code = ex is DomainException domain ? domain.Code : "not_found";
            logger.LogInformation(
                "Marketplace sync of org {OrgId}: the license of property {PropertyId} was not reconciled ({Error}), left for the next run", orgId, propertyId, code);
        }
    }

    private async Task ReadQuotaAsync(Guid orgId, RunCounts run, CancellationToken ct)
    {
        try
        {
            OctorateConfiguration configuration;
            using (orgScope.Use(orgId))
                configuration = await client.GetConfigurationAsync(ct);

            var quota = MarketplaceHealthRules.QuotaOf(configuration, options.Value.EffectiveQuotaWarnPercent, Now());
            if (quota is null)
            {
                cache.Remove(QuotaKey(orgId));
                return;
            }

            cache.Set(QuotaKey(orgId), quota, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = QuotaLifetime });
            if (quota.Warn)
            {
                logger.LogWarning(
                    "Marketplace sync of org {OrgId}: {Used} of {Limit} units of the quota of the channel manager's API are used", orgId, quota.Used, quota.Limit);
            }
        }
        catch (OctorateRateLimitException)
        {
            throw;
        }
        catch (OctorateException ex)
        {
            run.Failed++;
            logger.LogInformation(
                "Marketplace sync of org {OrgId}: the quota was not read ({Error}), the one of the last run is kept", orgId, ex.Code);
        }
    }

    private async Task ReconcileTodosAsync(Guid orgId, Guid propertyId, RunCounts run, CancellationToken ct)
    {
        var rows = await db.MarketplaceActivations
            .Where(a => a.OrgId == orgId
                        && a.PropertyId == propertyId
                        && HealthKinds.Contains(a.Kind)
                        && ((a.Status == MarketplaceActivationStatus.ToFix && a.TodoActivityId == null)
                            || (a.Status != MarketplaceActivationStatus.ToFix && a.TodoActivityId != null)))
            .ToListAsync(ct);
        if (rows.Count == 0)
            return;

        try
        {
            foreach (var row in rows)
            {
                if (row.Status == MarketplaceActivationStatus.ToFix)
                {
                    try
                    {
                        row.TodoActivityId = await activities.CreateAsync(TodoOf(row), ct);
                        run.TodoRaised++;
                    }
                    catch (NotFoundException)
                    {
                        logger.LogInformation("Marketplace activation {ActivationId} of org {OrgId}: no To do raised, its property is gone", row.Id, orgId);
                    }

                    continue;
                }

                try
                {
                    await activities.CloseAsync(row.TodoActivityId!.Value, InternalActivity.ClosedBySystem, ct);
                    run.TodoClosed++;
                }
                catch (NotFoundException)
                {
                    logger.LogInformation("Marketplace activation {ActivationId} of org {OrgId}: its To do is gone, the reference is cleared", row.Id, orgId);
                }

                row.TodoActivityId = null;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            ForgetTrackedRows();
            run.Skipped++;
            logger.LogInformation(
                "Marketplace sync of property {PropertyId} of org {OrgId}: the activations were changed while the To do items were set, left for the next run", propertyId, orgId);
        }
    }

    private static CreateTodoRequest TodoOf(MarketplaceActivation row) =>
        new()
        {
            Type = ActivityType.IntegrationError,

            Action = ActivityAction.ResumeWizard,
            EntityType = TodoEntityType,
            EntityId = row.Id,
            PropertyId = row.PropertyId,

            Urgency = row.Kind == MarketplaceActivationKind.Channel ? ActivityUrgency.High : ActivityUrgency.Medium,
            Reason = row.LastErrorCode,
        };

    private async Task<Guid?> ConnectedAccountAsync(Guid orgId, CancellationToken ct)
    {
        var environment = OctorateEnvironments.Of(hostEnvironment);
        return await db.OctorateConnections.AsNoTracking()
            .Where(c => c.OrgId == orgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
    }

    private void ForgetTrackedRows()
    {
        foreach (var entry in db.ChangeTracker.Entries<MarketplaceActivation>().ToList())
            entry.State = EntityState.Detached;
    }

    private static string Printable(string? word)
    {
        var text = word?.Trim();
        if (string.IsNullOrEmpty(text))
            return "none";

        var printable = new string([.. text.Where(c => !char.IsControl(c)).Take(40)]);
        return printable.Length == 0 ? "none" : printable;
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record PropertyPlan(Guid PropertyId, string? AccommodationId, bool HasConnections, bool HasFunctions);

    private sealed class RunCounts
    {
        public int Properties { get; set; }

        public int Updated { get; set; }

        public int Unrecognised { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public int TodoRaised { get; set; }

        public int TodoClosed { get; set; }

        public MarketplaceSyncResult ToResult() => new(Properties, Updated, Unrecognised, Skipped, Failed, TodoRaised, TodoClosed);
    }
}
