using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Manages the internal activities of an org: the work performed by staff (collaborators) on properties (NF-22) and the
/// items of the "To do" list (ADR-026, NF-07), which share the table <c>InternalActivities</c>. A row of the staff work has no
/// <see cref="InternalActivity.Action"/>; a row of the To do always has one. The methods of the staff work read and change only
/// staff rows, the methods of the To do only To do rows.
/// Activities are isolated by perimeter: staff sees only activities on properties they can access.
/// </summary>
public interface IInternalActivityService
{
    /// <summary>
    /// Creates a new internal activity (cleaning, maintenance, check-in assistance).
    /// </summary>
    /// <param name="propertyId">The property where the activity will be performed.</param>
    /// <param name="staffUserId">The staff user assigned to the activity.</param>
    /// <param name="activityType">Type of activity (Cleaning, Maintenance, CheckIn).</param>
    /// <param name="scheduledAt">When the activity is scheduled (UTC).</param>
    /// <param name="internalRate">Optional internal rate per activity or per person.</param>
    /// <param name="rechargeToOwner">Whether the cost should be recharged to the owner.</param>
    /// <param name="bookingId">Optional booking this activity is related to.</param>
    /// <param name="notes">Optional notes about the activity.</param>
    /// <param name="createdByUserId">Who is creating the activity.</param>
    /// <returns>The created activity.</returns>
    Task<InternalActivity> CreateActivityAsync(
        Guid propertyId,
        string staffUserId,
        string activityType,
        DateTime scheduledAt,
        decimal? internalRate = null,
        bool rechargeToOwner = false,
        Guid? bookingId = null,
        string? notes = null,
        string? createdByUserId = null);

    /// <summary>
    /// Updates the status of an activity of the staff work and optionally records completion.
    /// </summary>
    /// <param name="activityId">The activity to update.</param>
    /// <param name="status">The new status (Pending, InProgress, Completed).</param>
    /// <param name="notes">Optional notes to append or update.</param>
    /// <returns>The updated activity.</returns>
    Task<InternalActivity> UpdateStatusAsync(Guid activityId, string status, string? notes = null);

    /// <summary>
    /// Gets all activities of the staff work within the caller's perimeter (filtered by HostScope).
    /// Staff with "Only some" perimeter sees only activities on their assigned properties.
    /// </summary>
    /// <param name="scope">The caller's property scope.</param>
    /// <param name="propertyId">Optional property filter.</param>
    /// <param name="staffUserId">Optional staff user filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activities the caller can see.</returns>
    Task<IReadOnlyList<InternalActivity>> GetActivitiesAsync(
        HostScope scope,
        Guid? propertyId = null,
        string? staffUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets activities of the staff work assigned to the calling staff user.
    /// </summary>
    /// <param name="scope">The caller's property scope.</param>
    /// <param name="staffUserId">The staff user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activities assigned to this staff user within their perimeter.</returns>
    Task<IReadOnlyList<InternalActivity>> GetMyActivitiesAsync(
        HostScope scope,
        string staffUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single activity of the staff work by ID.
    /// </summary>
    /// <param name="activityId">The activity ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activity, or null if not found.</returns>
    Task<InternalActivity?> GetActivityAsync(Guid activityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The open "To do" items the scope reaches (ADR-026-R2): the To do rows of <see cref="HostScope.OrgId"/> not closed yet,
    /// narrowed to the properties of the scope (an organization-level item, with no property, only for an org-wide scope; the
    /// items of a soft-deleted property are not listed), the most urgent first, then by deadline (the ones without a deadline
    /// last), then by creation; at most <see cref="TodoLimits.MaxPendingItems"/>. The single source of the list for web,
    /// mobile, email and push.
    /// </summary>
    Task<IReadOnlyList<InternalActivity>> GetPendingAsync(HostScope scope, CancellationToken ct);

    /// <summary>How many of the open items <see cref="GetPendingAsync"/> reaches are <see cref="ActivityUrgency.High"/>: the nav badge.</summary>
    Task<int> CountUrgentAsync(HostScope scope, CancellationToken ct);

    /// <summary>
    /// Raises a "To do" item and returns its id, which the caller keeps to close the item later. Idempotent: while an item of
    /// the same <see cref="CreateTodoRequest.Type"/>, <see cref="CreateTodoRequest.EntityType"/> and
    /// <see cref="CreateTodoRequest.EntityId"/> is open in the org, the id of that item is returned and nothing is created (also
    /// for two parallel calls: the second loses the race on a unique index and returns the first). Once that item is closed, the
    /// next call raises a new one.
    /// </summary>
    /// <remarks>
    /// The org of the item is the org of <see cref="CreateTodoRequest.PropertyId"/> or, for an organization-level item (no
    /// property), <see cref="CreateTodoRequest.OrgId"/>; never a value of a client. A property that is not found (or not in the
    /// current tenant) is a <see cref="Exceptions.NotFoundException"/>; a request whose <see cref="CreateTodoRequest.OrgId"/>
    /// is not the org of its property, or that has neither, is an <see cref="ArgumentException"/>.
    /// </remarks>
    Task<Guid> CreateAsync(CreateTodoRequest request, CancellationToken ct);

    /// <summary>
    /// Raises an open "To do" (E05/F05/US02/T01): its urgency becomes <paramref name="urgency"/> when that is higher than the one it has (never lower), and its deadline becomes
    /// <paramref name="deadline"/> when that is earlier than the one it has (or it has none); when the urgency was raised, <paramref name="reason"/> (the code of the more urgent
    /// cause) replaces the reason of the item. How a producer whose item is one per entity while it is open (<see cref="CreateAsync"/> returns the open one and changes nothing)
    /// keeps that item as urgent as the most urgent thing that happened since it was raised. Returns <c>true</c> when the item changed; <c>false</c> for an item that is already
    /// as urgent and as soon, one closed meanwhile, and an id that is not an open "To do" of the org. Idempotent.
    /// </summary>
    Task<bool> RaiseUrgencyAsync(Guid activityId, ActivityUrgency urgency, DateTime? deadline, string? reason, CancellationToken ct);

    /// <summary>
    /// Closes a "To do" item: <see cref="InternalActivity.Status"/> "Completed", <see cref="InternalActivity.CompletedAt"/> now
    /// and <see cref="InternalActivity.ClosedBy"/> = <paramref name="closedBy"/> (<see cref="InternalActivity.ClosedBySystem"/>
    /// or the id of the user). Idempotent: an item already closed stays as it was closed the first time. The closed row is kept
    /// for the audit and leaves the list. An id that is not a "To do" (unknown, or a row of the staff work) is a
    /// <see cref="Exceptions.NotFoundException"/>.
    /// </summary>
    Task CloseAsync(Guid activityId, string closedBy, CancellationToken ct);

    /// <summary>
    /// <see cref="CloseAsync"/> for a user who closes an item of the list by hand: the item must be one the scope reaches
    /// (<see cref="GetPendingAsync"/>, closed ones included); any other id is a <see cref="Exceptions.NotFoundException"/>, the
    /// same answer as an id that does not exist.
    /// </summary>
    Task CloseInScopeAsync(Guid activityId, HostScope scope, string closedBy, CancellationToken ct);

    /// <summary>
    /// The open "To do" of an org with this <paramref name="action"/> (an <see cref="ActivityAction"/> member name) on this entity, or
    /// <c>null</c> when there is none (E03/F02/US01/T03): how a producer that keeps the list in step with a condition (the compliance cockpit) finds
    /// the item to close when the condition clears, without creating one. A closed item is not found. No scope is applied: the caller names the
    /// org and the entity, and the web layer decides who may ask. An action that is not a member of <see cref="ActivityAction"/> is an
    /// <see cref="ArgumentException"/>, never a quiet <c>null</c>.
    /// </summary>
    Task<InternalActivity?> FindOpenAsync(Guid orgId, string action, string entityType, Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// <see cref="FindOpenAsync(Guid, string, string, Guid, CancellationToken)"/> for several entities of one type in one read: the open items
    /// keyed by the entity id (the oldest, should the list ever hold two). An empty list of entities is an empty answer, never "all of them".
    /// </summary>
    Task<IReadOnlyDictionary<Guid, InternalActivity>> FindOpenAsync(
        Guid orgId,
        string action,
        string entityType,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct = default);

    /// <summary>
    /// The open "To do" with this <paramref name="action"/> on an entity of this <paramref name="entityType"/> that the <paramref name="scope"/> reaches
    /// (the same reach as the list: its org, the properties it reaches), except those about <paramref name="exceptEntityIds"/>: how the compliance
    /// cockpit finds the items of stays it no longer evaluates (cancelled, no-show, past the lookback) to close them. At most
    /// <see cref="TodoLimits.MaxPendingItems"/> times ten, the oldest first, so a backlog is closed over several runs. An action that is not a member of
    /// <see cref="ActivityAction"/> is an <see cref="ArgumentException"/>.
    /// </summary>
    Task<IReadOnlyList<InternalActivity>> FindOpenInScopeAsync(
        HostScope scope,
        string action,
        string entityType,
        IReadOnlyCollection<Guid> exceptEntityIds,
        CancellationToken ct = default);
}

/// <summary>
/// A "To do" item to raise (<see cref="IInternalActivityService.CreateAsync"/>, ADR-026). No personal data: the item names
/// the entity it is about by type and id, and the frontend reads the detail from the entity's own API (ADR-026-R3).
/// </summary>
public sealed record CreateTodoRequest
{
    /// <summary>What the item is.</summary>
    public required ActivityType Type { get; init; }

    /// <summary>Where its "Do now" button leads.</summary>
    public required ActivityAction Action { get; init; }

    /// <summary>The kind of entity it is about (<see cref="InternalActivity.BookingEntityType"/>, ...), at most 50 characters.</summary>
    public required string EntityType { get; init; }

    /// <summary>The id of that entity: with the type and the entity type, the key of the idempotence.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>How urgent it is.</summary>
    public required ActivityUrgency Urgency { get; init; }

    /// <summary>The property it is about; the org of the item is the property's. <c>null</c> for an organization-level item.</summary>
    public Guid? PropertyId { get; init; }

    /// <summary>
    /// The org of an organization-level item (no <see cref="PropertyId"/>). With a property it may be left out; when given it
    /// must be the property's org.
    /// </summary>
    public Guid? OrgId { get; init; }

    /// <summary>When it is due (UTC), if it has a deadline.</summary>
    public DateTime? Deadline { get; init; }

    /// <summary>Why it was raised, one line (a code such as <c>octorate_checkin_failed</c>): never personal data. At most 500 characters.</summary>
    public string? Reason { get; init; }

    /// <summary>The user it is for, when it is for one person (kept in <see cref="InternalActivity.StaffUserId"/>, never in the list's answer).</summary>
    public string? ForUserId { get; init; }
}

/// <summary>The limits of the "To do" list (ADR-026).</summary>
public static class TodoLimits
{
    /// <summary>The most items <see cref="IInternalActivityService.GetPendingAsync"/> returns (ADR-026: paginated, max 50 items).</summary>
    public const int MaxPendingItems = 50;

    /// <summary>The longest <see cref="InternalActivity.EntityType"/>.</summary>
    public const int EntityTypeMaxLength = 50;

    /// <summary>The longest <see cref="InternalActivity.Reason"/>.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>The longest <see cref="InternalActivity.ClosedBy"/> and <see cref="InternalActivity.StaffUserId"/>.</summary>
    public const int UserIdMaxLength = 255;
}
