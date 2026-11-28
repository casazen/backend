using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The import of the listings that already exist on OTAs or in another channel manager, the <c>onOtas</c> scenario of the connect wizard
/// (E02/F01/US03, D6, ADR-023, ADR-027). It guides the access to each channel through the Octorate panel, or connects the channel from CasaZen
/// when the portal needs only the id of the property on it («Collega da CasaZen», <see cref="ConnectChannelAsync"/>), imports the existing
/// inventory keeping its identifiers, hands the imported listings to <see cref="IChannelMappingService"/> and goes on with the reservations and
/// the calendars. It never creates or publishes a replacement listing on Octorate or on an OTA.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Durable and idempotent.</b> Every step is written before it runs and every result after it; the stamps of the import say what is
/// done, so a repeated command, a callback delivered twice or a restart of the worker resumes from the last durable step and imports or
/// marks each external item at most once (also across batches: the stamps of a connection are inherited).</item>
/// <item><b>Fail closed.</b> A timeout, an unknown result, an ambiguous mapping, a rate limit or a failed channel becomes
/// <see cref="OctorateInventoryImportStatus.NeedsAttention"/> or <see cref="OctorateInventoryImportStatus.Failed"/> with a stable code
/// (<c>OctorateInventoryErrorCodes</c>). An operation not verified on a real property for the channel (<c>Octorate:ExistingInventory</c>) is
/// not called and stops with <c>d6_operation_unverified</c>. There is no fallback to <see cref="OctorateInventoryScenario.NewInventory"/>.</item>
/// <item><b>One creation, on the host's word.</b> The only thing this service ever creates is the connection of a channel on an accommodation
/// that the host asks for with «Collega», for a portal that needs only the id of the property on it, and never a second connection of the same
/// channel on an accommodation. No state, error or retry of the import creates a property, a listing or a replacement connection.</item>
/// <item><b>No secret.</b> The access to a channel is given in the Octorate panel, or on the extranet of the OTA after «Collega»: no username,
/// password, cookie or OTA token enters CasaZen, and no response carries one, nor the address of the panel once the access has started. The id
/// of a property on a portal is not a secret (the portal shows it to the host): it goes to Octorate in the create and is not kept.</item>
/// <item><b>One org, and the reach of the caller.</b> Every method reads only rows of the caller's org. A batch is within the reach of its
/// caller: the whole inventory needs a caller who reaches the whole org, and the properties of a batch (or of its group) must be reachable
/// by whoever acts on it. An id of another org is the same 404 as an unknown one. A caller who does not reach the whole org sees, decides and
/// moves only the listings of the properties it reaches (the rule of the mapping list): the others are not in the answers and are never touched
/// in its name.</item>
/// </list>
/// </remarks>
public interface IExistingInventoryImportService
{
    /// <summary>
    /// Creates a batch for the OTA <paramref name="channelCodes"/> (portal codes of the catalog: unknown or unlisted ones are refused) and
    /// the properties of <paramref name="scope"/>: the whole inventory (the caller reaches the whole org), the properties it named (each of
    /// the org and within its reach) or a named group of the mixed portfolio. The properties named, or in the group, are declared
    /// <c>onOtas</c> in the inventory intents. Nothing is called on Octorate.
    /// </summary>
    Task<ExistingInventoryBatchView> CreateBatchAsync(
        ExistingInventoryCaller caller,
        IReadOnlyCollection<string> channelCodes,
        ExistingInventoryScope scope,
        CancellationToken ct);

    Task<ExistingInventoryBatchView> GetBatchAsync(ExistingInventoryCaller caller, Guid batchId, CancellationToken ct);

    /// <summary>
    /// Starts (or reuses) the durable <c>channelAccess</c> handoff of the channel, writes the resume point and the channel's state BEFORE it
    /// returns the handoff mode and the address of the Octorate panel, and moves the channel to
    /// <see cref="OctorateInventoryImportStatus.AwaitingChannelAccess"/>. CasaZen accepts no credential.
    /// </summary>
    Task<ChannelAccessStartResult> StartChannelAccessAsync(ExistingInventoryCaller caller, Guid batchId, string channelCode, CancellationToken ct);

    /// <summary>
    /// «Collega da CasaZen» (decision of the product owner, 14/11/2026): for a portal whose connection needs only the id of the property on it
    /// (<see cref="MarketplaceCardRules.ConnectsByPropertyId"/>), creates on Octorate, on the host's explicit request, the connection of the
    /// channel on each accommodation of <paramref name="accommodations"/> that has none (the pulls off, no content pushed), records on the channel
    /// of the batch every accommodation connected or found already connected, then starts the access step as
    /// <see cref="StartChannelAccessAsync"/> does. The host's only step left is to approve Octorate on the extranet of the OTA: the step is verified
    /// when Octorate shows one of those connections <c>ENABLED</c>. An accommodation that already has a connection of the channel never gets a
    /// second one. A channel whose access is already given is returned as it is.
    /// </summary>
    /// <remarks>
    /// 422 <c>existing_inventory_connect_empty</c> (no accommodation, or more than 50), <c>existing_inventory_hotel_id_invalid</c> (an id of 1 to 64
    /// letters, digits, dots, underscores or hyphens is needed, and each accommodation once), <c>existing_inventory_accommodation_unknown</c> (not one
    /// of the org's accommodations CasaZen knows, a listing the person rejected, or, for a batch of named properties or of a group, not confirmed to
    /// one of its properties) and <c>existing_inventory_connect_not_supported</c> (the portal needs more than the id); 404 for a batch or a channel
    /// the caller does not have. A refusal of Octorate is its own error, as for every other call, and the connections made before it stay recorded.
    /// </remarks>
    Task<ExistingInventoryBatchView> ConnectChannelAsync(
        ExistingInventoryCaller caller,
        Guid batchId,
        string channelCode,
        IReadOnlyCollection<ExistingInventoryConnectTarget> accommodations,
        CancellationToken ct);

    Task<ExistingInventoryBatchView> ConfirmChannelAccessAsync(ExistingInventoryCaller caller, Guid batchId, string channelCode, CancellationToken ct);

    /// <summary>
    /// Imports the rooms of every channel whose access is given (<c>IMPORT_ROOMS</c> on each of its connections), refreshes
    /// <see cref="IChannelMappingService"/> and stops at <see cref="OctorateInventoryImportStatus.AwaitingMapping"/>. Idempotent: what has a
    /// stamp is not asked again. It never creates a property, a listing or a connection, and never pushes content. An operation whose result is
    /// unknown (<c>d6_result_unknown</c>: the answer never came, or the request was closed after the call left) is not asked again by itself:
    /// <paramref name="retryUnknown"/> names (by accommodation id) the listings a person checked in the panel and wants asked again.
    /// </summary>
    Task<ExistingInventoryBatchView> RunAsync(
        ExistingInventoryCaller caller,
        Guid batchId,
        CancellationToken ct,
        IReadOnlyCollection<string>? retryUnknown = null);

    /// <summary>
    /// Goes on for the listings of the batch that a person confirmed (or imported as a CasaZen property): their reservations
    /// (<c>IMPORT_RESERVATIONS</c> and the import into CasaZen), calendars (<c>IMPORT_CALENDAR</c>) and <c>MARK_CONNECTED</c>. Allowed only when
    /// every listing is confirmed, rejected, or left for later: <paramref name="leaveForLater"/> names (by accommodation id) the ones the
    /// person leaves for later now; they stay write-blocked and are not marked connected, while the others go on. 409
    /// <c>existing_inventory_mapping_pending</c> when one is none of those. An operation whose result is unknown (<c>d6_result_unknown</c>) is not
    /// asked again by itself: <paramref name="retryUnknown"/> names (by accommodation id) the listings a person checked in the panel and wants
    /// asked again.
    /// </summary>
    Task<ExistingInventoryBatchView> ContinueAsync(
        ExistingInventoryCaller caller,
        Guid batchId,
        IReadOnlyCollection<string>? leaveForLater,
        CancellationToken ct,
        IReadOnlyCollection<string>? retryUnknown = null);

    /// <summary>
    /// Records the scenario of each assignment (a property of the org within the caller's reach, or a named group of the mixed portfolio).
    /// A property has one current intent; moving one to <see cref="OctorateInventoryScenario.NewInventory"/> is refused while an import batch
    /// or a mapping exists for it (409 <c>inventory_intent_change_refused</c>): it is never an automatic recovery.
    /// </summary>
    Task<IReadOnlyList<InventoryIntentView>> SetIntentsAsync(
        ExistingInventoryCaller caller,
        IReadOnlyCollection<InventoryIntentAssignment> assignments,
        CancellationToken ct);

    Task<IReadOnlyList<InventoryIntentView>> ListIntentsAsync(ExistingInventoryCaller caller, CancellationToken ct);
}

/// <summary>Who acts: the org, the person (the Auth0 subject) and the roles of its token, which only decide a person in no org team.</summary>
/// <param name="OrgId">The caller's org.</param>
/// <param name="UserId">The caller's id.</param>
/// <param name="TokenRoles">The roles of the caller's token, exactly as the web layer decided them; <c>null</c> = none.</param>
public sealed record ExistingInventoryCaller(Guid OrgId, string UserId, IReadOnlySet<string>? TokenRoles = null);

/// <summary>One accommodation of a «Collega da CasaZen»: where the connection is created and the id of the property on the portal.</summary>
/// <param name="AccommodationId">The Octorate accommodation the connection of the channel is created on: one of the org's that CasaZen knows.</param>
/// <param name="HotelId">
/// The id of the property on the portal (<c>hotelId</c>: the «ID struttura» of Booking.com), 1 to 64 letters, digits, dots, underscores or hyphens
/// once trimmed. Not a secret: the portal shows it to the host on its extranet. It goes to Octorate in the create and is not kept.
/// </param>
public sealed record ExistingInventoryConnectTarget(string? AccommodationId, string? HotelId);

/// <summary>
/// The guard that keeps existing inventory from being created again (E02/F01/US03, consumed by E02/F03/US01/T06): a listing is created on
/// Octorate only for a property whose inventory scenario a person explicitly declared <see cref="OctorateInventoryScenario.NewInventory"/>.
/// A missing declaration, a missing mapping or any other scenario refuses it.
/// </summary>
public interface IInventoryScenarioGuard
{
    Task<OctorateInventoryScenario?> GetScenarioAsync(Guid orgId, Guid propertyId, CancellationToken ct);

    /// <summary>
    /// Throws 409 <c>octorate_existing_inventory_must_import</c> unless the scenario of the property is explicitly
    /// <see cref="OctorateInventoryScenario.NewInventory"/>: existing inventory (<c>onOctorate</c>, <c>onOtas</c>) is imported and mapped,
    /// and absence of a mapping never proves it is new.
    /// </summary>
    Task EnsureNewInventoryAsync(Guid orgId, Guid propertyId, CancellationToken ct);
}

/// <summary>
/// The durable handoff of the person to the Octorate panel for the access to one channel (the <c>channelAccess</c> step of the panel steps of
/// epic E10, NF-107). The import starts it, reads its result and, only after the check stored «could not tell», takes the person's
/// confirmation. The minimal implementation keeps the step in <c>OnboardingPanelStep</c> and checks the access by reading the connections
/// of the org; the panel steps service of E10 replaces it by registering its own.
/// </summary>
public interface IChannelAccessHandoff
{
    /// <summary>
    /// Creates or reuses the step of the org for the channel, writes the hand-over and the resume point (the batch and the channel) and only
    /// then returns what the client needs to open the panel. Idempotent. The address carries no organization, property or person data.
    /// </summary>
    Task<ChannelAccessHandoffStart> StartAsync(Guid orgId, string channelCode, ChannelAccessResumePoint resume, CancellationToken ct);

    Task<ChannelAccessSnapshot> GetAsync(Guid orgId, string channelCode, bool verifyIfDue, CancellationToken ct);

    /// <summary>
    /// Takes the confirmation of the person. 409 <c>panel_step_verification_required</c> unless a check stored «could not tell»; 409
    /// <c>panel_step_not_valid</c> after a check that found the access not valid. Repeating it is harmless.
    /// </summary>
    Task ConfirmAsync(Guid orgId, string userId, string channelCode, CancellationToken ct);
}

public sealed record ChannelAccessResumePoint(Guid BatchId, string ChannelCode);

/// <summary>What the client needs to open the panel: how, where, and the step it will come back to.</summary>
/// <param name="StepId">The step stored for the access.</param>
/// <param name="HandoffMode">Whether the panel returns to a CasaZen callback or opens in a window CasaZen manages.</param>
/// <param name="PanelUrl">The address of the Octorate panel: absolute https, carrying no organization, property or person data.</param>
public sealed record ChannelAccessHandoffStart(Guid StepId, PanelHandoffMode HandoffMode, string PanelUrl);

/// <summary>The stored state of the access to a channel.</summary>
/// <param name="StepId">The step, or <c>null</c> when the person was never sent to the panel.</param>
/// <param name="State">Where the access stands.</param>
public sealed record ChannelAccessSnapshot(Guid? StepId, ChannelAccessState State)
{
    public bool IsComplete => State is ChannelAccessState.Verified or ChannelAccessState.Confirmed;
}

public sealed record ChannelAccessStartResult(string ChannelCode, bool AlreadyComplete, PanelHandoffMode? HandoffMode, string? PanelUrl);

public sealed record ExistingInventoryScope(OctorateInventoryScopeKind Kind, IReadOnlyList<Guid> PropertyIds, string? Group)
{
    public static ExistingInventoryScope AllInventory { get; } = new(OctorateInventoryScopeKind.AllInventory, [], null);

    public static ExistingInventoryScope ForProperties(IReadOnlyList<Guid> propertyIds) =>
        new(OctorateInventoryScopeKind.Properties, propertyIds, null);

    public static ExistingInventoryScope ForGroup(string group) => new(OctorateInventoryScopeKind.Group, [], group);
}

public sealed record InventoryIntentAssignment(Guid? PropertyId, OctorateInventoryScenario Scenario, string? Group);

public sealed record InventoryIntentView(Guid? PropertyId, OctorateInventoryScenario Scenario, string? Group, DateTime UpdatedAt);

/// <summary>An import batch as the API shows it: progress, counts, stable error codes and safe ids; never a credential or a payload.</summary>
public sealed record ExistingInventoryBatchView(
    Guid Id,
    OctorateInventoryScopeKind ScopeKind,
    string? Group,
    IReadOnlyList<Guid> PropertyIds,
    OctorateInventoryImportStatus Status,
    string? ErrorCode,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<ExistingInventoryChannelView> Channels);

public sealed record ExistingInventoryChannelView(
    string ChannelCode,
    OctorateInventoryImportStatus Status,
    string? ErrorCode,
    ChannelAccessState AccessState,
    DateTime? AccessCompletedAt,
    DateTime? RoomsImportedAt,
    DateTime? CompletedAt,
    ExistingInventoryCounts Counts,
    ExistingInventoryCapabilities Capabilities,
    IReadOnlyList<ExistingInventoryItemView> Items,
    IReadOnlyList<string> ConnectedAccommodationIds);

/// <summary>The counts of the listings of a channel.</summary>
/// <param name="Found">Listings with a connection to the channel.</param>
/// <param name="RoomsImported">With the rooms imported.</param>
/// <param name="Confirmed">Confirmed (or imported) to a CasaZen property.</param>
/// <param name="Pending">Waiting for a person to decide.</param>
/// <param name="LeftForLater">Left for later: write-blocked, not marked connected.</param>
/// <param name="Rejected">Rejected: not one of the org's properties.</param>
/// <param name="OutOfScope">Confirmed to a property that is not in the scope of the batch: left alone.</param>
/// <param name="ReservationsImported">With the reservations imported into CasaZen.</param>
/// <param name="CalendarImported">With the calendar imported.</param>
/// <param name="Connected">Marked as connected.</param>
/// <param name="Failed">With an error code.</param>
public sealed record ExistingInventoryCounts(
    int Found,
    int RoomsImported,
    int Confirmed,
    int Pending,
    int LeftForLater,
    int Rejected,
    int OutOfScope,
    int ReservationsImported,
    int CalendarImported,
    int Connected,
    int Failed);

public sealed record ExistingInventoryCapabilities(bool ImportRooms, bool ImportReservations, bool ImportCalendar, bool MarkConnected);

/// <summary>
/// A listing of a channel with its progress: the ids Octorate gave and the state of its mapping; never a name, an address, a payload or the
/// CasaZen property (the mapping endpoints show that, within the reach of the caller).
/// </summary>
public sealed record ExistingInventoryItemView(
    string OctorateAccommodationId,
    string OctorateConnectionId,
    OctorateMappingStatus? MappingStatus,
    bool LeftForLater,
    bool RoomsImported,
    int RoomCount,
    bool ReservationsImported,
    bool CalendarImported,
    bool Connected,
    string? ErrorCode);
