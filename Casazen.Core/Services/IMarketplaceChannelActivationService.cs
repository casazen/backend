using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The steps behind the activation wizard of a sales channel or a connected tool through the channel manager's API (E09/F02/US01/T02, NF-56,
/// MKT-03, D6): plan, connection, import, mapping, activation. Not yet proven on a real channel (pilot): the shapes are the ones of the OpenAPI and the
/// tests use a stub of the client. The whole feature sits behind <c>Features:OctorateConnector</c>, and an operation of the D6 sequence is called only
/// when its flag for the channel is on (<c>Octorate:ExistingInventory:Channels</c>, all off by default): otherwise the step is a 409 <c>d6_operation_unverified</c>.
/// </summary>
/// <remarks>
/// <para><b>Every step</b> checks the same four things, in this order: the scope is the one of the org (<c>ArgumentException</c> otherwise: fail closed),
/// the property is inside the caller's <see cref="HostScope"/> (the 404 of a property that does not exist, for another org's, for one outside the scope
/// and for one that was deleted), the org has a connected Octorate account in this environment, and the property has a confirmed listing there whose
/// accommodation id is the <c>acc</c> of every call. An activation is looked up the same way: one of another org, or of a property outside the scope,
/// is <c>marketplace_activation_not_found</c>.</para>
/// <para><b>Credentials.</b> The username and password of the portal travel to the channel manager in one call and are never stored (not in
/// <c>MarketplaceActivation</c>, not in a draft) and never logged.</para>
/// <para><b>Only Octorate decides.</b> An activation is Active only when the channel manager lists its connection as working after «mark connected»; the
/// calendar and reservation pulls of the connection are switched on only after the rooms are mapped (D6). A read (<see cref="GetStateAsync"/>) writes
/// nothing.</para>
/// </remarks>
public interface IMarketplaceChannelActivationService
{
    Task<MarketplaceActivationPlan> GetPlanAsync(
        Guid orgId,
        HostScope scope,
        MarketplaceActivationKind kind,
        string portalCode,
        Guid propertyId,
        CancellationToken ct = default);

    /// <summary>
    /// Creates the connection on the channel manager with the access data of <paramref name="request"/> and the activation (Pending), under an
    /// advisory lock keyed by org, property and portal. Idempotent: an activation of the same org, property, kind and code that has its connection is
    /// returned and nothing is created. A channel manager that already holds a connection of the channel for the property, which CasaZen did not make,
    /// is <c>marketplace_connection_exists</c>.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// <c>marketplace_property_not_linked</c>, <c>marketplace_portal_not_creatable</c>, <c>marketplace_credentials_required</c>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><c>marketplace_octorate_not_connected</c>, <c>marketplace_connection_exists</c>.</exception>
    Task<MarketplaceActivationState> StartAsync(
        Guid orgId,
        HostScope scope,
        MarketplaceActivationStart request,
        CancellationToken ct = default);

    Task<MarketplaceActivationState> GetStateAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default);

    Task<MarketplaceActivationState> ImportAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default);

    Task<MarketplaceActivationMapping> GetMappingAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default);

    Task<MarketplaceActivationState> SaveMappingAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        IReadOnlyList<MarketplaceRoomChoice> choices,
        CancellationToken ct = default);

    Task<MarketplaceActivationState> ActivateAsync(Guid orgId, HostScope scope, Guid activationId, CancellationToken ct = default);
}
