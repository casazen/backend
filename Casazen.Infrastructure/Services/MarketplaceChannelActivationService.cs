using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplaceChannelActivationService"/> (E09/F02/US01/T02, NF-56, MKT-03, D6): the steps of the activation wizard of a sales channel or a
/// connected tool, over the operations of <see cref="IOctorateApiClient"/> on a connection. It keeps no copy of what the channel manager knows: the
/// state of a step is read from the channel manager, and the row of <see cref="MarketplaceActivation"/> keeps only what the cards of the marketplace
/// show (the status, the connection id, the last sync and the last error code).
/// </summary>
/// <remarks>
/// <para><b>Credentials.</b> The username and password reach <see cref="StartAsync"/> inside <see cref="MarketplaceActivationStart"/>, go to the
/// channel manager in one call and are forgotten: no column, no draft, no log line, no message holds them.</para>
/// <para><b>One connection.</b> The start runs under an advisory lock keyed by org, property and portal. Under it the activation is looked up; one that
/// has its connection is returned. Otherwise the connections of the accommodation are listed first: the channel manager has no idempotency key, so a
/// connection of the channel that already exists is never joined by a second one. If the activation row exists without its connection (an earlier
/// start whose answer was lost) the connection found is taken; if there is no row, the connection was not made by CasaZen and is a 409. When the
/// answer of the create is lost the row is kept without its id, so the next start finds the connection in the list.</para>
/// <para><b>Only the channel manager decides.</b> Active is written only when it lists the connection as working after «mark connected»; the pulls of the
/// connection are switched on after that, and only once at least one room is mapped (D6). A failure of the channel itself (550) leaves the activation To fix
/// with its code and is raised again; a failure worth a retry (rate limit, no answer) changes nothing and is raised.</para>
/// </remarks>
public sealed class MarketplaceChannelActivationService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<MarketplaceChannelActivationService> logger) : IMarketplaceChannelActivationService
{
    /// <summary>The longest portal code CasaZen sends as the number Octorate calls <c>channelId</c>.</summary>
    private const int MaxPortalCodeDigits = 18;

    /// <summary>The imports of the D6 sequence, in order: the rooms first (they are what is mapped), then the reservations and the calendar.</summary>
    private static readonly OctorateConnectionAction[] ImportActions =
    [
        OctorateConnectionAction.ImportRooms,
        OctorateConnectionAction.ImportReservations,
        OctorateConnectionAction.ImportCalendar,
    ];

    public async Task<MarketplaceActivationPlan> GetPlanAsync(
        Guid orgId,
        HostScope scope,
        MarketplaceActivationKind kind,
        string portalCode,
        Guid propertyId,
        CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var portal = await FindPortalAsync(kind, portalCode, ct);
        await MarketplaceReads.EnsurePropertyInScopeAsync(db, orgId, scope, propertyId, ct);
        var connectionKey = await ConnectedAccountAsync(orgId, ct) ?? throw NotConnected();

        var linked = await LinkOfAsync(orgId, propertyId, connectionKey, ct) is not null;
        var existing = await db.MarketplaceActivations.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.PropertyId == propertyId && a.Kind == kind && a.TargetCode == portal.PortalCode)
            .Select(a => new { a.Id, a.Status })
            .FirstOrDefaultAsync(ct);

        var requirements = new List<string>();
        if (portal.RequiresHotelId)
            requirements.Add(MarketplaceActivationRequirements.HotelId);

        if (portal.RequiresPassword)
            requirements.Add(MarketplaceActivationRequirements.Credentials);

        if (portal.RequiresTokenAuthentication)
            requirements.Add(MarketplaceActivationRequirements.PanelAccess);

        return new MarketplaceActivationPlan(
            kind,
            portal.PortalCode,
            portal.Name,
            propertyId,
            requirements,
            IsCreatable(portal),
            linked,
            existing?.Id,
            existing?.Status);
    }

    public async Task<MarketplaceActivationState> StartAsync(
        Guid orgId,
        HostScope scope,
        MarketplaceActivationStart request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var portal = await FindPortalAsync(request.Kind, request.PortalCode, ct);
        await MarketplaceReads.EnsurePropertyInScopeAsync(db, orgId, scope, request.PropertyId, ct);
        var link = await RequireLinkAsync(orgId, request.PropertyId, ct);
        if (!IsCreatable(portal))
            throw new DomainRuleException(MarketplaceActivationErrorCodes.PortalNotCreatable, "MarketplacePortalNotCreatable");

        // One start of this property and portal at a time. The lock is held until the commit, the calls to the channel manager included: the
        // request that waits then finds the activation of the one before it. Outside PostgreSQL there is no lock.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            ct,
            (PostgresAdvisoryLocks.Scope.MarketplaceChannelActivation, $"{orgId:N}:{request.PropertyId:N}:{portal.PortalCode}"));

        var activation = await db.MarketplaceActivations.FirstOrDefaultAsync(
            a => a.OrgId == orgId && a.PropertyId == request.PropertyId && a.Kind == request.Kind && a.TargetCode == portal.PortalCode,
            ct);

        if (activation is { OctorateConnectionId: not null } && activation.Status != MarketplaceActivationStatus.Deactivated)
        {
            logger.LogInformation("Marketplace activation {ActivationId} was started already: returned as it is, nothing is created", activation.Id);
            return await StateAsync(orgId, link, activation, ct);
        }

        // What the portal asks for is checked only when a connection is to be made: a second start that carries no credentials (the wizard keeps none)
        // still gets the activation that exists.
        var create = ConnectionDataOf(portal, request);

        OctorateConnectionInfo[] listed;
        using (orgScope.Use(orgId))
            listed = await client.ListConnectionsAsync(link.AccommodationId, ct);

        var found = listed.FirstOrDefault(c => string.Equals(c.PortalCode, portal.PortalCode, StringComparison.Ordinal));
        if (found is not null && activation is null)
            throw new DomainConflictException(MarketplaceActivationErrorCodes.ConnectionExists, "MarketplaceConnectionExists");

        var now = Now();
        var isNew = activation is null;
        if (activation is null)
        {
            activation = new MarketplaceActivation
            {
                OrgId = orgId,
                PropertyId = request.PropertyId,
                Kind = request.Kind,
                TargetCode = portal.PortalCode,
                RequestedAt = now,
                CreatedAt = now,
            };
            db.MarketplaceActivations.Add(activation);
        }

        var connection = found;
        if (connection is null)
        {
            try
            {
                using (orgScope.Use(orgId))
                    connection = await client.CreateConnectionAsync(link.AccommodationId, create, ct);
            }
            catch (OctorateServerException ex)
            {
                // Whether the channel manager made the connection is not known: the activation is kept without its id, so that the next start
                // finds the connection in the list and takes it instead of making a second one.
                activation.Status = MarketplaceActivationStatus.Pending;
                activation.OctorateConnectionId = null;
                activation.LastErrorCode = ex.Code;
                activation.LastErrorAt = now;
                activation.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                if (transaction is not null)
                    await transaction.CommitAsync(ct);

                logger.LogWarning(
                    "Marketplace activation {ActivationId} of org {OrgId}: the connection was not confirmed ({Error}), the next start looks for it",
                    activation.Id, orgId, ex.Code);
                throw;
            }
            catch (OctorateConflictException)
            {
                Forget(activation, isNew);
                throw new DomainConflictException(MarketplaceActivationErrorCodes.ConnectionExists, "MarketplaceConnectionExists");
            }
            catch
            {
                // A refusal (access data, the channel failed, the rate limit): nothing was made, nothing is kept.
                Forget(activation, isNew);
                throw;
            }
        }

        activation.OctorateConnectionId = connection.ConnectionId;
        activation.Status = MarketplaceActivationStatus.Pending;
        activation.IsDeclared = false;
        activation.DeclaredAt = null;
        activation.VerifiedAt = null;
        activation.LastSyncedAt = now;
        activation.LastErrorCode = null;
        activation.LastErrorAt = null;
        activation.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Marketplace activation {ActivationId} of org {OrgId} started for property {PropertyId}, portal {PortalCode}, connection {ConnectionId}",
            activation.Id, orgId, request.PropertyId, portal.PortalCode, connection.ConnectionId);
        return StateOf(activation, connection);
    }

    public async Task<MarketplaceActivationState> GetStateAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default)
    {
        var activation = await FindActivationAsync(orgId, scope, activationId, track: false, ct);
        var link = await RequireLinkAsync(orgId, activation.PropertyId!.Value, ct);
        return await StateAsync(orgId, link, activation, ct);
    }

    public async Task<MarketplaceActivationState> ImportAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default)
    {
        var activation = await FindActivationAsync(orgId, scope, activationId, track: true, ct);
        var link = await RequireLinkAsync(orgId, activation.PropertyId!.Value, ct);
        var connection = await RequireConnectionAsync(orgId, link, activation, ct);
        await EnsurePanelAccessAsync(activation, connection, ct);
        var connectionId = activation.OctorateConnectionId!;

        await WithChannelManagerAsync(
            orgId,
            activation,
            async () =>
            {
                foreach (var action in ImportActions)
                    await client.RunConnectionActionAsync(link.AccommodationId, connectionId, action, ct);
            },
            ct);

        // The imports are asynchronous there: the state is read again, and a failure of the channel recorded earlier no longer stands.
        var fresh = await FindConnectionAsync(orgId, link, activation, ct);
        var now = Now();
        await SaveAsync(
            activation,
            a =>
            {
                if (a.Status == MarketplaceActivationStatus.ToFix)
                {
                    a.Status = MarketplaceActivationStatus.Pending;
                    a.LastErrorCode = null;
                    a.LastErrorAt = null;
                }

                a.LastSyncedAt = now;
                a.UpdatedAt = now;
            },
            ct);

        logger.LogInformation("Marketplace activation {ActivationId} of org {OrgId}: rooms, reservations and calendar imported", activation.Id, orgId);
        return StateOf(activation, fresh);
    }

    public async Task<MarketplaceActivationMapping> GetMappingAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default)
    {
        var activation = await FindActivationAsync(orgId, scope, activationId, track: false, ct);
        var link = await RequireLinkAsync(orgId, activation.PropertyId!.Value, ct);
        var connectionId = ConnectionIdOf(activation);

        OctorateChannelRoom[] rooms;
        OctorateRoom[] products;
        using (orgScope.Use(orgId))
        {
            rooms = await client.GetConnectionMappingAsync(link.AccommodationId, connectionId, ct);
            products = await client.GetRoomsAsync(link.AccommodationId, ct);
        }

        var proposals = ChannelRoomMappingProposer.Propose(rooms, products);
        return new MarketplaceActivationMapping(
            activation.Id,
            link.ProductId,
            [
                .. rooms.Select(room => new MarketplaceChannelRoom(
                    room.ChannelRoomId,
                    room.Name,
                    room.RateName,
                    room.Manageable,
                    room.MappedProductId,
                    proposals.TryGetValue(room.ChannelRoomId, out var proposed) ? proposed : null)),
            ],
            [.. products.Select(product => new MarketplaceProduct(product.Id, product.Name))]);
    }

    public async Task<MarketplaceActivationState> SaveMappingAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        IReadOnlyList<MarketplaceRoomChoice> choices,
        CancellationToken ct = default)
    {
        var activation = await FindActivationAsync(orgId, scope, activationId, track: true, ct);

        // An empty choice is not «all the rooms»: nothing leaves.
        if (choices is null || choices.Count == 0)
            throw new DomainRuleException(MarketplaceActivationErrorCodes.MappingRequired, "MarketplaceMappingRequired");

        var link = await RequireLinkAsync(orgId, activation.PropertyId!.Value, ct);
        var connectionId = ConnectionIdOf(activation);

        if (choices.Any(c => c is null || string.IsNullOrWhiteSpace(c.ChannelRoomId) || string.IsNullOrWhiteSpace(c.ProductId))
            || choices.Select(c => c.ChannelRoomId.Trim()).Distinct(StringComparer.Ordinal).Count() != choices.Count)
        {
            throw new DomainRuleException(MarketplaceActivationErrorCodes.MappingInvalid, "MarketplaceMappingInvalid");
        }

        await WithChannelManagerAsync(
            orgId,
            activation,
            async () =>
            {
                // The rooms and the products are read again: a choice is checked against what the channel manager holds now, never against what the
                // page showed.
                var rooms = await client.GetConnectionMappingAsync(link.AccommodationId, connectionId, ct);
                var products = await client.GetRoomsAsync(link.AccommodationId, ct);
                var manageable = rooms.Where(room => room.Manageable).Select(room => room.ChannelRoomId).ToHashSet(StringComparer.Ordinal);
                var offered = products.Select(product => product.Id).ToHashSet(StringComparer.Ordinal);
                if (choices.Any(c => !manageable.Contains(c.ChannelRoomId.Trim()) || !offered.Contains(c.ProductId.Trim())))
                    throw new DomainRuleException(MarketplaceActivationErrorCodes.MappingInvalid, "MarketplaceMappingInvalid");

                await client.SaveConnectionMappingAsync(
                    link.AccommodationId,
                    connectionId,
                    [.. choices.Select(c => new OctorateRoomMapping(c.ProductId.Trim(), c.ChannelRoomId.Trim()))],
                    ct);
                await client.RunConnectionActionAsync(link.AccommodationId, connectionId, OctorateConnectionAction.CommitMappingAsCompleted, ct);
            },
            ct);

        var fresh = await FindConnectionAsync(orgId, link, activation, ct);
        var now = Now();
        await SaveAsync(
            activation,
            a =>
            {
                if (a.Status == MarketplaceActivationStatus.ToFix)
                {
                    a.Status = MarketplaceActivationStatus.Pending;
                    a.LastErrorCode = null;
                    a.LastErrorAt = null;
                }

                a.LastSyncedAt = now;
                a.UpdatedAt = now;
            },
            ct);

        logger.LogInformation("Marketplace activation {ActivationId} of org {OrgId}: {Rooms} rooms mapped", activation.Id, orgId, choices.Count);
        return StateOf(activation, fresh);
    }

    public async Task<MarketplaceActivationState> ActivateAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default)
    {
        var activation = await FindActivationAsync(orgId, scope, activationId, track: true, ct);
        var link = await RequireLinkAsync(orgId, activation.PropertyId!.Value, ct);
        var current = await RequireConnectionAsync(orgId, link, activation, ct);
        await EnsurePanelAccessAsync(activation, current, ct);

        // Already on and working: a second click changes nothing.
        if (activation.Status == MarketplaceActivationStatus.Active && current.IsConnected)
            return StateOf(activation, current);

        var connectionId = activation.OctorateConnectionId!;
        OctorateConnectionInfo? verified = null;
        await WithChannelManagerAsync(
            orgId,
            activation,
            async () =>
            {
                // D6: the pulls of the connection start only after the mapping.
                var rooms = await client.GetConnectionMappingAsync(link.AccommodationId, connectionId, ct);
                if (!rooms.Any(room => room.MappedProductId is not null))
                    throw new DomainRuleException(MarketplaceActivationErrorCodes.MappingRequired, "MarketplaceMappingRequired");

                await client.RunConnectionActionAsync(link.AccommodationId, connectionId, OctorateConnectionAction.MarkConnected, ct);

                // The proof is what the channel manager lists, not the answer of the action.
                verified = Match(await client.ListConnectionsAsync(link.AccommodationId, ct), activation);
                if (verified is { IsConnected: true } && (verified.UpdateCalendar != true || verified.PullReservations != true))
                {
                    await client.UpdateConnectionAsync(
                        link.AccommodationId,
                        connectionId,
                        new OctorateConnectionPatch { UpdateCalendar = true, PullReservations = true },
                        ct);
                }
            },
            ct);

        var now = Now();
        if (verified is { IsConnected: true })
        {
            await SaveAsync(
                activation,
                a =>
                {
                    a.Status = MarketplaceActivationStatus.Active;
                    a.IsDeclared = false;
                    a.DeclaredAt = null;
                    a.VerifiedAt = now;
                    a.LastSyncedAt = now;
                    a.LastErrorCode = null;
                    a.LastErrorAt = null;
                    a.UpdatedAt = now;
                },
                ct);
            logger.LogInformation("Marketplace activation {ActivationId} of org {OrgId} is active", activation.Id, orgId);
        }
        else
        {
            var code = verified is null ? MarketplaceActivationErrorCodes.ConnectionLost : MarketplaceActivationErrorCodes.ConnectionNotEnabled;
            await SetToFixAsync(activation, code, ct, now);
            logger.LogWarning("Marketplace activation {ActivationId} of org {OrgId} is not active: {Error}", activation.Id, orgId, code);
        }

        return StateOf(activation, verified);
    }

    // ─── The checks every step shares ────────────────────────────────────────────────────────────────────────

    /// <summary>The listing a property is confirmed to: its accommodation id (the <c>acc</c> of every call) and the room the connect wizard chose.</summary>
    private sealed record Link(string AccommodationId, string? ProductId);

    /// <summary>
    /// The activation of the caller: one of the org, of a channel or a tool, whose property is inside the scope. Another org's, one outside the scope
    /// and one that does not exist are the same 404.
    /// </summary>
    private async Task<MarketplaceActivation> FindActivationAsync(Guid orgId, HostScope scope, Guid activationId, bool track, CancellationToken ct)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        IQueryable<MarketplaceActivation> activations = track ? db.MarketplaceActivations : db.MarketplaceActivations.AsNoTracking();
        var activation = await activations.FirstOrDefaultAsync(
            a => a.Id == activationId
                 && a.OrgId == orgId
                 && a.PropertyId != null
                 && (a.Kind == MarketplaceActivationKind.Channel || a.Kind == MarketplaceActivationKind.Tool),
            ct);
        if (activation is null)
            throw ActivationNotFound();

        var reachable = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == activation.PropertyId)
            .InScope(scope)
            .AnyAsync(ct);
        return reachable ? activation : throw ActivationNotFound();
    }

    /// <summary>The portal of the catalog the wizard connects: listed, enabled and of the category of <paramref name="kind"/>; else the 404 of a code nobody has.</summary>
    private async Task<MarketplacePortal> FindPortalAsync(MarketplaceActivationKind kind, string? portalCode, CancellationToken ct)
    {
        var code = portalCode?.Trim();
        if (string.IsNullOrEmpty(code) || kind is not (MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool))
            throw PortalUnknown();

        var category = kind == MarketplaceActivationKind.Channel ? MarketplacePortalCategory.Ota : MarketplacePortalCategory.ExternalTool;
        var portal = await db.MarketplacePortals.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PortalCode == code && p.IsListed && p.Enabled && p.Category == category, ct);
        return portal ?? throw PortalUnknown();
    }

    /// <summary>The org is connected (the connector is on and it has a connected account in this environment) and the property has its confirmed listing.</summary>
    private async Task<Link> RequireLinkAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var connectionKey = await ConnectedAccountAsync(orgId, ct) ?? throw NotConnected();
        return await LinkOfAsync(orgId, propertyId, connectionKey, ct) ?? throw NotLinked();
    }

    /// <summary>The id of the org's connected account in this environment; <c>null</c> while the connector is off or the org is not connected.</summary>
    private async Task<Guid?> ConnectedAccountAsync(Guid orgId, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return null;

        var environment = OctorateEnvironments.Of(hostEnvironment);
        return await db.OctorateConnections.AsNoTracking()
            .Where(c => c.OrgId == orgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The confirmed listing of the property that was found through the connected account of this environment (a listing found through another
    /// environment's account has an accommodation id that does not exist here).
    /// </summary>
    private async Task<Link?> LinkOfAsync(Guid orgId, Guid propertyId, Guid connectionKey, CancellationToken ct) =>
        await db.OctorateListingMappings.AsNoTracking()
            .Where(m => m.OrgId == orgId
                        && m.PropertyId == propertyId
                        && m.ConnectionId == connectionKey
                        && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => new Link(m.OctorateAccommodationId, m.OctorateProductId))
            .FirstOrDefaultAsync(ct);

    private static string ConnectionIdOf(MarketplaceActivation activation) =>
        activation.OctorateConnectionId is { Length: > 0 } id
            ? id
            : throw new DomainConflictException(MarketplaceActivationErrorCodes.ConnectionMissing, "MarketplaceConnectionMissing");

    /// <summary>The connection of the activation as the channel manager lists it; a connection that is not there is the 409 that leads to a new start.</summary>
    private async Task<OctorateConnectionInfo> RequireConnectionAsync(Guid orgId, Link link, MarketplaceActivation activation, CancellationToken ct)
    {
        _ = ConnectionIdOf(activation);
        return await FindConnectionAsync(orgId, link, activation, ct)
               ?? throw new DomainConflictException(MarketplaceActivationErrorCodes.ConnectionMissing, "MarketplaceConnectionMissing");
    }

    private async Task<OctorateConnectionInfo?> FindConnectionAsync(Guid orgId, Link link, MarketplaceActivation activation, CancellationToken ct)
    {
        if (activation.OctorateConnectionId is null)
            return null;

        OctorateConnectionInfo[] listed;
        using (orgScope.Use(orgId))
            listed = await client.ListConnectionsAsync(link.AccommodationId, ct);

        return Match(listed, activation);
    }

    private static OctorateConnectionInfo? Match(IEnumerable<OctorateConnectionInfo> listed, MarketplaceActivation activation) =>
        listed.FirstOrDefault(
            c => string.Equals(c.ConnectionId, activation.OctorateConnectionId, StringComparison.Ordinal)
                 && string.Equals(c.PortalCode, activation.TargetCode, StringComparison.Ordinal));

    /// <summary>A channel that needs the access given on the channel manager's panel is not asked for imports or activation before the person gave it.</summary>
    private async Task EnsurePanelAccessAsync(MarketplaceActivation activation, OctorateConnectionInfo connection, CancellationToken ct)
    {
        var needsPanel = await db.MarketplacePortals.AsNoTracking()
            .Where(p => p.PortalCode == activation.TargetCode)
            .Select(p => p.RequiresTokenAuthentication)
            .FirstOrDefaultAsync(ct);
        if (needsPanel && !connection.PanelAccessDone)
            throw new DomainConflictException(MarketplaceActivationErrorCodes.PanelAccessPending, "MarketplacePanelAccessPending");
    }

    private async Task<MarketplaceActivationState> StateAsync(Guid orgId, Link link, MarketplaceActivation activation, CancellationToken ct) =>
        StateOf(activation, await FindConnectionAsync(orgId, link, activation, ct));

    private static MarketplaceActivationState StateOf(MarketplaceActivation activation, OctorateConnectionInfo? connection) =>
        new(
            activation.Id,
            activation.Kind,
            activation.TargetCode,
            activation.PropertyId!.Value,
            activation.Status,
            connection is not null,
            connection?.Status,
            connection?.PanelAccessDone ?? false,
            activation.LastSyncedAt,
            activation.LastErrorCode,
            connection?.LastUpdate);

    // ─── Calls to the channel manager and what they leave behind ─────────────────────────────────────────────

    /// <summary>
    /// Runs the calls of one step for <paramref name="orgId"/>. A failure of the channel itself (550) is kept on the activation as To fix with its code
    /// and raised again; any other failure is raised and changes nothing (a retry is the answer to it).
    /// </summary>
    private async Task WithChannelManagerAsync(Guid orgId, MarketplaceActivation activation, Func<Task> work, CancellationToken ct)
    {
        try
        {
            using (orgScope.Use(orgId))
                await work();
        }
        catch (OctoratePartnerFailureException ex)
        {
            logger.LogWarning(
                "Marketplace activation {ActivationId} of org {OrgId}: the channel failed ({Error}, HTTP {StatusCode})",
                activation.Id, orgId, ex.Code, ex.OctorateStatusCode);
            await SetToFixAsync(activation, ex.Code, ct);
            throw;
        }
    }

    private async Task SetToFixAsync(MarketplaceActivation activation, string code, CancellationToken ct, DateTime? readAt = null)
    {
        if (activation.Status == MarketplaceActivationStatus.Deactivated)
            return;

        var now = Now();
        await SaveAsync(
            activation,
            a =>
            {
                a.Status = MarketplaceActivationStatus.ToFix;
                a.LastErrorCode = code.Length <= MarketplaceActivation.LastErrorCodeMaxLength ? code : code[..MarketplaceActivation.LastErrorCodeMaxLength];
                a.LastErrorAt = now;
                a.LastSyncedAt = readAt ?? a.LastSyncedAt;
                a.UpdatedAt = now;
            },
            ct);
    }

    /// <summary>
    /// Applies <paramref name="change"/> and saves. A row changed by another request meanwhile (the version token of the row) is read again and the
    /// change applied on top once: the steps are idempotent, so the later writer wins instead of the request failing.
    /// </summary>
    private async Task SaveAsync(MarketplaceActivation activation, Action<MarketplaceActivation> change, CancellationToken ct)
    {
        change(activation);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(activation).ReloadAsync(ct);
            change(activation);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>An activation that was added in this request and failed leaves nothing behind (not even in the tracker); a row that existed is untouched.</summary>
    private void Forget(MarketplaceActivation activation, bool isNew)
    {
        if (isNew)
            db.Entry(activation).State = EntityState.Detached;
    }

    // ─── Rules of the portal ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Octorate lets a connection to the portal be created through its API, and the portal code is the number it calls <c>channelId</c>. A code that
    /// is not a number cannot be sent: the wizard cannot start for it.
    /// </summary>
    private static bool IsCreatable(MarketplacePortal portal) =>
        portal.Creatable && portal.PortalCode.Length is > 0 and <= MaxPortalCodeDigits && portal.PortalCode.All(char.IsAsciiDigit);

    /// <summary>
    /// What goes to the channel manager: only what the portal asks for (the identifier of the property, the username and password) and the person gave;
    /// anything more in the request is dropped. What the portal asks for and is missing is the 422 the wizard shows on the step.
    /// </summary>
    private static OctorateConnectionCreate ConnectionDataOf(MarketplacePortal portal, MarketplaceActivationStart request)
    {
        string? hotelId = null;
        string? username = null;
        string? password = null;
        if (portal.RequiresHotelId)
        {
            hotelId = request.HotelId?.Trim();
            if (string.IsNullOrEmpty(hotelId))
                throw CredentialsRequired();
        }

        if (portal.RequiresPassword)
        {
            username = request.Username?.Trim();
            password = request.Password;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                throw CredentialsRequired();
        }

        return new OctorateConnectionCreate(portal.PortalCode, hotelId, username, password);
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static DomainConflictException NotConnected() =>
        new DomainConflictException(MarketplaceActivationErrorCodes.OctorateNotConnected, "MarketplaceOctorateNotConnected");

    private static DomainRuleException NotLinked() =>
        new DomainRuleException(MarketplaceActivationErrorCodes.PropertyNotLinked, "MarketplacePropertyNotLinked");

    private static DomainRuleException CredentialsRequired() =>
        new DomainRuleException(MarketplaceActivationErrorCodes.CredentialsRequired, "MarketplaceCredentialsRequired");

    private static NotFoundException PortalUnknown() =>
        new("The portal is not a channel or a tool of the catalog.")
        {
            Code = MarketplaceActivationErrorCodes.PortalUnknown,
            MessageKey = "MarketplacePortalUnknown",
        };

    private static NotFoundException ActivationNotFound() =>
        new("The activation is not one of the organization inside the scope of the caller.")
        {
            Code = MarketplaceActivationErrorCodes.ActivationNotFound,
            MessageKey = "MarketplaceActivationNotFound",
        };
}
