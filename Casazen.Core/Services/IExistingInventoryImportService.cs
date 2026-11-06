using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The import of the listings that already exist on OTAs or in another channel manager, the <c>onOtas</c> scenario of the connect wizard
/// (E02/F01/US03, D6, ADR-023, ADR-027). It guides the access to each channel through the Octorate panel, imports the existing inventory
/// keeping its identifiers, hands the imported listings to <see cref="IChannelMappingService"/> and goes on with the reservations and the
/// calendars. It never creates or publishes a replacement listing on Octorate or on an OTA.
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
/// <item><b>No secret.</b> The access to a channel is given in the Octorate panel: no username, password, cookie or OTA token enters CasaZen,
/// and no response carries one, nor the address of the panel once the access has started.</item>
/// <item><b>One org, and the reach of the caller.</b> Every method reads only rows of the caller's org. A batch is within the reach of its
/// caller: the whole inventory needs a caller who reaches the whole org, and the properties of a batch (or of its group) must be reachable
/// by whoever acts on it. An id of another org is the same 404 as an unknown one.</item>
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

    /// <summary>
    /// The batch as stored, after reading the result of the channel access of each channel that is waiting for it (a check of the access is
    /// made at most when its backoff is due). A verified access moves the channel on to the room import.
    /// </summary>
    Task<ExistingInventoryBatchView> GetBatchAsync(ExistingInventoryCaller caller, Guid batchId, CancellationToken ct);

    /// <summary>
    /// Starts (or reuses) the durable <c>channelAccess</c> handoff of the channel, writes the resume point and the channel's state BEFORE it
    /// returns the handoff mode and the address of the Octorate panel, and moves the channel to
    /// <see cref="OctorateInventoryImportStatus.AwaitingChannelAccess"/>. CasaZen accepts no credential.
    /// </summary>
    Task<ChannelAccessStartResult> StartChannelAccessAsync(ExistingInventoryCaller caller, Guid batchId, string channelCode, CancellationToken ct);

    /// <summary>
    /// The person says the access was given. Accepted only after the check stored «could not tell» for the channel; refused (409
    /// <c>panel_step_verification_required</c>) before it and after a check that found the access not valid (409 <c>panel_step_not_valid</c>).
    /// </summary>
    Task<ExistingInventoryBatchView> ConfirmChannelAccessAsync(ExistingInventoryCaller caller, Guid batchId, string channelCode, CancellationToken ct);

    /// <summary>
    /// Imports the rooms of every channel whose access is given (<c>IMPORT_ROOMS</c> on each of its connections), refreshes
    /// <see cref="IChannelMappingService"/> and stops at <see cref="OctorateInventoryImportStatus.AwaitingMapping"/>. Idempotent: what has a
    /// stamp is not asked again. It never creates a property, a listing or a connection, and never pushes content.
    /// </summary>
    Task<ExistingInventoryBatchView> RunAsync(ExistingInventoryCaller caller, Guid batchId, CancellationToken ct);

    /// <summary>
    /// Goes on for the listings of the batch that a person confirmed (or imported as a CasaZen property): their reservations
    /// (<c>IMPORT_RESERVATIONS</c> and the import into CasaZen), calendars (<c>IMPORT_CALENDAR</c>) and <c>MARK_CONNECTED</c>. Allowed only when
    /// every listing is confirmed, rejected, or left for later: <paramref name="leaveForLater"/> names (by accommodation id) the ones the
    /// person leaves for later now; they stay write-blocked and are not marked connected, while the others go on. 409
    /// <c>existing_inventory_mapping_pending</c> when one is none of those.
    /// </summary>
    Task<ExistingInventoryBatchView> ContinueAsync(
        ExistingInventoryCaller caller,
        Guid batchId,
        IReadOnlyCollection<string>? leaveForLater,
        CancellationToken ct);

    /// <summary>
    /// Records the scenario of each assignment (a property of the org within the caller's reach, or a named group of the mixed portfolio).
    /// A property has one current intent; moving one to <see cref="OctorateInventoryScenario.NewInventory"/> is refused while an import batch
    /// or a mapping exists for it (409 <c>inventory_intent_change_refused</c>): it is never an automatic recovery.
    /// </summary>
    Task<IReadOnlyList<InventoryIntentView>> SetIntentsAsync(
        ExistingInventoryCaller caller,
        IReadOnlyCollection<InventoryIntentAssignment> assignments,
        CancellationToken ct);

    /// <summary>The intents of the org the caller reaches (the properties it reaches; those of the groups with no property only when it reaches the whole org).</summary>
    Task<IReadOnlyList<InventoryIntentView>> ListIntentsAsync(ExistingInventoryCaller caller, CancellationToken ct);
}

/// <summary>Who acts: the org, the person (the Auth0 subject) and the roles of its token, which only decide a person in no org team.</summary>
/// <param name="OrgId">The caller's org.</param>
/// <param name="UserId">The caller's id.</param>
/// <param name="TokenRoles">The roles of the caller's token, exactly as the web layer decided them; <c>null</c> = none.</param>
public sealed record ExistingInventoryCaller(Guid OrgId, string UserId, IReadOnlySet<string>? TokenRoles = null);

/// <summary>
/// The guard that keeps existing inventory from being created again (E02/F01/US03, consumed by E02/F03/US01/T06): a listing is created on
/// Octorate only for a property whose inventory scenario a person explicitly declared <see cref="OctorateInventoryScenario.NewInventory"/>.
/// A missing declaration, a missing mapping or any other scenario refuses it.
/// </summary>
public interface IInventoryScenarioGuard
{
    /// <summary>The scenario declared for the property of the org; <c>null</c> when nobody declared one.</summary>
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

    /// <summary>The stored state of the step; when <paramref name="verifyIfDue"/> and the backoff is due, one check of the access runs first.</summary>
    Task<ChannelAccessSnapshot> GetAsync(Guid orgId, string channelCode, bool verifyIfDue, CancellationToken ct);

    /// <summary>
    /// Takes the confirmation of the person. 409 <c>panel_step_verification_required</c> unless a check stored «could not tell»; 409
    /// <c>panel_step_not_valid</c> after a check that found the access not valid. Repeating it is harmless.
    /// </summary>
    Task ConfirmAsync(Guid orgId, string userId, string channelCode, CancellationToken ct);
}

/// <summary>Where the person comes back to after the panel: the batch and the channel of the import.</summary>
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
    /// <summary>The access is given: seen by CasaZen, or confirmed by the person after CasaZen could not check it.</summary>
    public bool IsComplete => State is ChannelAccessState.Verified or ChannelAccessState.Confirmed;
}

/// <summary>The answer of the start of the access to a channel: the only place the address of the panel is ever returned.</summary>
/// <param name="ChannelCode">The channel.</param>
/// <param name="AlreadyComplete">The access was already given: nothing to open.</param>
/// <param name="HandoffMode">How the panel is opened; <c>null</c> when <paramref name="AlreadyComplete"/>.</param>
/// <param name="PanelUrl">The address to open; <c>null</c> when <paramref name="AlreadyComplete"/>.</param>
public sealed record ChannelAccessStartResult(string ChannelCode, bool AlreadyComplete, PanelHandoffMode? HandoffMode, string? PanelUrl);

/// <summary>Which properties a batch is about; build it with the factories.</summary>
/// <param name="Kind">The kind of scope.</param>
/// <param name="PropertyIds">The properties named, for <see cref="OctorateInventoryScopeKind.Properties"/>.</param>
/// <param name="Group">The named group, for <see cref="OctorateInventoryScopeKind.Group"/>.</param>
public sealed record ExistingInventoryScope(OctorateInventoryScopeKind Kind, IReadOnlyList<Guid> PropertyIds, string? Group)
{
    /// <summary>Every property of the org: the caller must reach the whole org.</summary>
    public static ExistingInventoryScope AllInventory { get; } = new(OctorateInventoryScopeKind.AllInventory, [], null);

    /// <summary>The properties named: each one of the org and within the reach of the caller.</summary>
    public static ExistingInventoryScope ForProperties(IReadOnlyList<Guid> propertyIds) =>
        new(OctorateInventoryScopeKind.Properties, propertyIds, null);

    /// <summary>A named group of the mixed portfolio.</summary>
    public static ExistingInventoryScope ForGroup(string group) => new(OctorateInventoryScopeKind.Group, [], group);
}

/// <summary>One assignment of a scenario: a property (or, with none, a named group of properties not in CasaZen yet) and what the person declared.</summary>
/// <param name="PropertyId">The CasaZen property; <c>null</c> for a group that has no property yet.</param>
/// <param name="Scenario">The declared scenario.</param>
/// <param name="Group">The named group of the mixed portfolio; required when there is no property.</param>
public sealed record InventoryIntentAssignment(Guid? PropertyId, OctorateInventoryScenario Scenario, string? Group);

/// <summary>An intent as the API shows it.</summary>
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

/// <summary>A channel of a batch: where it stands, the counts of its listings and the listings themselves.</summary>
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
    IReadOnlyList<ExistingInventoryItemView> Items);

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

/// <summary>Which operations of the D6 sequence are verified for the channel: an operation that is not stops with <c>d6_operation_unverified</c> and stays guided.</summary>
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
