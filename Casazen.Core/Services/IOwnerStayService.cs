using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The privacy-safe calendar of the owner portal and the governed owner stays (E07/F07/US01/T01, NF-111, ADR-011).
/// </summary>
/// <remarks>
/// <para><b>Calendar.</b> Dates and a neutral kind only (<see cref="OwnerCalendarKinds"/>): no guest id, name, contact, document, message, channel or text
/// (AC-NF111-1), and only the days of the ownerships in force of the caller on the property.</para>
/// <para><b>Stays.</b> A request obeys the rule of the property (<see cref="OwnerStayRules"/>: notice, window, nights in a year, shortest and longest stay,
/// blocked dates, active period) and the free dates (no booking, no block, no other owner stay of the property, co-owners included) before it is
/// stored; it blocks nothing until it is approved (by a person, or by CasaZen when the rule does not ask for one). The approval creates ONE manual
/// block with the id of the stay through <c>ICalendarBlockService</c>, the path towards the channel manager; a withdrawal or a cancellation removes that
/// block and nothing else. No guest message, no invoice and no tourist tax is ever made for an owner stay (AC-NF111-2).</para>
/// <para><b>Sync.</b> A stay is shown confirmed only once its block is acknowledged (kept in CasaZen for a property that is not on the channel manager,
/// or accepted by the channel manager); a failure is a «To do» of the agency and can be sent again; every request, decision, sync result and
/// cancellation is a line of the activity log.</para>
/// <para><b>Who.</b> The owner-portal methods take the <see cref="OwnerPortalAccess"/> of the caller (resolved at every request); the agency methods take the
/// org the web layer resolved for an org-wide caller. The services never check roles.</para>
/// </remarks>
public interface IOwnerStayService
{
    /// <summary>
    /// The calendar of a property from <paramref name="from"/> to <paramref name="to"/> (nights, both included; today and the next 90 days by default, at
    /// most 366), cut to the ownerships in force of the caller on it. Needs <c>calendar.read</c> on the property.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="OwnerPortalErrors.PropertyNotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="OwnerStayErrors.CalendarRangeInvalid"/>.</exception>
    Task<OwnerCalendar> GetCalendarAsync(
        OwnerPortalAccess access,
        Guid propertyId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default);

    /// <summary>The stays of the owner profiles the caller reaches with <c>calendar.read</c> or <c>stays.request</c>, newest arrival first.</summary>
    Task<IReadOnlyList<OwnerStayView>> ListForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    /// <summary>The rule a request on the property follows for the caller's owner profile, and the nights already held this year. Needs <c>stays.request</c>.</summary>
    Task<OwnerStayRuleSummary> GetRuleForOwnerAsync(OwnerPortalAccess access, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for an owner stay. Needs <c>stays.request</c> on the property. Stored as <see cref="OwnerStayStatus.Requested"/>; when the rule does not ask for a
    /// person, CasaZen approves it in the background (the block is created there, never by the request).
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">A code of <see cref="OwnerStayErrors"/> for a rule the request breaks.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="OwnerStayErrors.DatesUnavailable"/>.</exception>
    Task<OwnerStayView> RequestAsync(OwnerPortalAccess access, OwnerStayRequestCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// The owner (or a contact with <c>stays.request</c> on the property) withdraws a stay that has not ended. A request is closed at once; an approved stay is
    /// cancelled at once and its block is given back in the background. A second call finds it done and answers the same.
    /// </summary>
    Task<OwnerStayView> CancelByOwnerAsync(OwnerPortalAccess access, Guid stayId, CancellationToken cancellationToken = default);

    /// <summary>The stays of the org, narrowed by state and property, newest arrival first (at most 500).</summary>
    Task<IReadOnlyList<OwnerStayAdminView>> ListForAgencyAsync(
        Guid orgId,
        OwnerStayStatus? status,
        Guid? propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a request: the dates must still be free; the block is created with the id of the stay (a repeat finds it done and answers the same).
    /// </summary>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="OwnerStayErrors.NotPending"/>, <see cref="OwnerStayErrors.DatesUnavailable"/>.</exception>
    Task<OwnerStayAdminView> ApproveAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Refuses a request; it never held a block. A repeat finds it done.</summary>
    Task<OwnerStayAdminView> RejectAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a stay that has not ended; an approved one gives its own block back at once. A repeat finds it done.</summary>
    Task<OwnerStayAdminView> CancelByAgencyAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends again the block (or its release) the channel manager refused. Idempotent: a stay with nothing failed answers as it is and sends nothing.
    /// </summary>
    Task<OwnerStayAdminView> RetrySyncAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>The rules of the org, or of one of its properties.</summary>
    Task<IReadOnlyList<OwnerStayRuleView>> ListRulesAsync(Guid orgId, Guid? propertyId, CancellationToken cancellationToken = default);

    /// <summary>Writes the rule of a property, or of one of its owners (one row each: a second save replaces the first).</summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="OwnerStayErrors.RuleInvalid"/>.</exception>
    /// <exception cref="Exceptions.NotFoundException">The property is not of the org, or the owner holds no ownership of it.</exception>
    Task<OwnerStayRuleView> SaveRuleAsync(Guid orgId, OwnerStayRuleInput input, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Removes a rule: the owner or the property falls back to the next one (the property's, then the default).</summary>
    Task DeleteRuleAsync(Guid orgId, Guid ruleId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The background work of the owner stays, bounded per run: approves the requests whose rule asks for no person, gives back the blocks of the stays
    /// withdrawn by an owner, records the sync result of every block and release that is not settled (with its line of the activity log), and keeps
    /// the «To do» of the agency in step (a request to decide, a sync that failed).
    /// </summary>
    Task<OwnerStayReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>The same work for one stay, run right after an owner request or withdrawal; the recurring run is the safety net.</summary>
    Task ProcessAsync(Guid stayId, CancellationToken cancellationToken = default);
}

/// <summary>Error codes of the owner stays, with the keys of their localized messages.</summary>
public static class OwnerStayErrors
{
    /// <summary>404: no such stay for the caller.</summary>
    public const string NotFound = "owner_stay_not_found";

    /// <summary>422: the departure is not after the arrival, or the stay is longer than a calendar block may be.</summary>
    public const string InvalidRange = "owner_stay_invalid_range";

    /// <summary>422: the arrival is before today (Europe/Rome).</summary>
    public const string InPast = "owner_stay_in_past";

    /// <summary>422: fewer nights than the rule allows ({0}).</summary>
    public const string TooShort = "owner_stay_too_short";

    /// <summary>422: more nights than the rule allows ({0}).</summary>
    public const string TooLong = "owner_stay_too_long";

    /// <summary>422: the arrival is closer than the notice of the rule ({0} days).</summary>
    public const string NoticeTooShort = "owner_stay_notice_too_short";

    /// <summary>422: the arrival is further than the window of the rule ({0} days).</summary>
    public const string BeyondWindow = "owner_stay_beyond_window";

    /// <summary>422: a night falls on dates the rule closes to owner stays.</summary>
    public const string Blackout = "owner_stay_blackout";

    /// <summary>422: a night falls outside the period in which the rule allows owner stays.</summary>
    public const string OutsidePeriod = "owner_stay_outside_period";

    /// <summary>422: the nights of the owner in a calendar year would pass the allowance of the rule ({0} nights in {1}).</summary>
    public const string AllowanceExceeded = "owner_stay_allowance_exceeded";

    /// <summary>409: a night is taken (a booking, a block, another owner stay of the property, a co-owner's included). Says nothing of whose.</summary>
    public const string DatesUnavailable = "owner_stay_dates_unavailable";

    /// <summary>409: the stay is not a request any more (decided, withdrawn).</summary>
    public const string NotPending = "owner_stay_not_pending";

    /// <summary>409: the stay is over, rejected, or cancelled differently.</summary>
    public const string NotCancellable = "owner_stay_not_cancellable";

    /// <summary>422: the days of a calendar: the last before the first, or more than 366.</summary>
    public const string CalendarRangeInvalid = "owner_calendar_range_invalid";

    /// <summary>422: a value of a rule out of bounds ({0}: the field).</summary>
    public const string RuleInvalid = "owner_stay_rule_invalid";

    /// <summary>404: no such rule in the org.</summary>
    public const string RuleNotFound = "owner_stay_rule_not_found";

    /// <summary>404: the property of a rule is not of the org.</summary>
    public const string PropertyNotFound = "owner_stay_property_not_found";

    /// <summary>404: the owner of a rule holds no ownership of the property.</summary>
    public const string OwnerNotFound = "owner_stay_owner_not_found";

    public const string NotFoundMessageKey = "OwnerStayNotFound";
    public const string InvalidRangeMessageKey = "OwnerStayInvalidRange";
    public const string InPastMessageKey = "OwnerStayInPast";
    public const string TooShortMessageKey = "OwnerStayTooShort";
    public const string TooLongMessageKey = "OwnerStayTooLong";
    public const string NoticeTooShortMessageKey = "OwnerStayNoticeTooShort";
    public const string BeyondWindowMessageKey = "OwnerStayBeyondWindow";
    public const string BlackoutMessageKey = "OwnerStayBlackout";
    public const string OutsidePeriodMessageKey = "OwnerStayOutsidePeriod";
    public const string AllowanceExceededMessageKey = "OwnerStayAllowanceExceeded";
    public const string DatesUnavailableMessageKey = "OwnerStayDatesUnavailable";
    public const string NotPendingMessageKey = "OwnerStayNotPending";
    public const string NotCancellableMessageKey = "OwnerStayNotCancellable";
    public const string CalendarRangeInvalidMessageKey = "OwnerCalendarRangeInvalid";
    public const string RuleInvalidMessageKey = "OwnerStayRuleInvalid";
    public const string RuleNotFoundMessageKey = "OwnerStayRuleNotFound";
    public const string PropertyNotFoundMessageKey = "OwnerStayPropertyNotFound";
    public const string OwnerNotFoundMessageKey = "OwnerStayOwnerNotFound";
}

/// <summary>The neutral kinds of the days of the owner calendar (AC-NF111-1): no guest, no channel, no text.</summary>
public static class OwnerCalendarKinds
{
    /// <summary>Free: an owner stay may be asked.</summary>
    public const string Available = "available";

    /// <summary>A stay of a guest takes the nights. Nothing else is said of it.</summary>
    public const string Occupied = "occupied";

    /// <summary>Closed (a block, the stay of a co-owner): nothing else is said of it.</summary>
    public const string Blocked = "blocked";

    /// <summary>A stay of the caller's owner profile, with its id and its state.</summary>
    public const string OwnerStay = "ownerStay";
}

/// <summary>How an owner stay reads in the portal and in the console (<see cref="OwnerStayRules.StateOf"/>).</summary>
public static class OwnerStayStates
{
    public const string Requested = "requested";

    /// <summary>Approved, the block is not acknowledged yet: never «confirmed».</summary>
    public const string ApprovedSyncing = "approved_syncing";

    /// <summary>Approved and its block acknowledged.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Approved, the channel manager refused the block: the agency sends it again.</summary>
    public const string SyncFailed = "sync_failed";

    public const string Rejected = "rejected";

    public const string Cancelled = "cancelled";
}

/// <summary>An owner stay to ask for: the arrival and the departure of the property.</summary>
public sealed record OwnerStayRequestCommand(Guid PropertyId, DateOnly StartDate, DateOnly EndDate);

/// <summary>A run of days of the owner calendar with the same kind.</summary>
/// <param name="Start">The first night.</param>
/// <param name="End">The day after the last night (excluded), as a stay counts it.</param>
/// <param name="Kind">One of <see cref="OwnerCalendarKinds"/>.</param>
/// <param name="StayId">The stay, for <see cref="OwnerCalendarKinds.OwnerStay"/> only.</param>
/// <param name="StayState">Its state (<see cref="OwnerStayStates"/>), for <see cref="OwnerCalendarKinds.OwnerStay"/> only.</param>
public sealed record OwnerCalendarRange(DateOnly Start, DateOnly End, string Kind, Guid? StayId = null, string? StayState = null);

/// <summary>The owner calendar of a property: the nights from <paramref name="From"/> to <paramref name="To"/> (both included) cut to the ownerships of the caller.</summary>
public sealed record OwnerCalendar(Guid PropertyId, DateOnly From, DateOnly To, IReadOnlyList<OwnerCalendarRange> Ranges);

/// <summary>Dates closed to owner stays: the nights from <paramref name="From"/> to <paramref name="To"/>, both included.</summary>
public sealed record OwnerStayBlackout(DateOnly From, DateOnly To);

/// <summary>An owner stay as the portal and the console read it. No guest, no price.</summary>
/// <param name="State">How it reads (<see cref="OwnerStayStates"/>), from the live state of its calendar change.</param>
/// <param name="SyncState">The live state of its block towards the channel manager.</param>
/// <param name="ReleaseSyncState">The live state of the release of its block.</param>
/// <param name="RequestedByContact">A contact the owner delegated asked for it, not the owner.</param>
public sealed record OwnerStayView(
    Guid Id,
    Guid OrgId,
    Guid PropertyId,
    string PropertyName,
    Guid PropertyOwnerId,
    DateOnly StartDate,
    DateOnly EndDate,
    int Nights,
    OwnerStayStatus Status,
    string State,
    OwnerStaySyncState SyncState,
    OwnerStaySyncState ReleaseSyncState,
    string? SyncErrorCode,
    DateTime RequestedAt,
    DateTime? DecidedAt,
    DateTime? CancelledAt,
    bool RequestedByContact);

/// <summary>An owner stay as the agency reads it: the stay and the name of its owner profile.</summary>
public sealed record OwnerStayAdminView(OwnerStayView Stay, string OwnerName, bool AutoApprove);

/// <summary>A rule of the owner stays (<see cref="Entities.OwnerStayRule"/>); <paramref name="Id"/> is <c>null</c> for the default of a property with no rule.</summary>
public sealed record OwnerStayRuleView(
    Guid? Id,
    Guid PropertyId,
    Guid? PropertyOwnerId,
    int? YearlyNightAllowance,
    int MinAdvanceDays,
    int? MaxAdvanceDays,
    int MinNights,
    int? MaxNights,
    bool ApprovalRequired,
    DateOnly? ActiveFrom,
    DateOnly? ActiveUntil,
    IReadOnlyList<OwnerStayBlackout> Blackouts,
    DateTime? UpdatedAt);

/// <summary>A rule to write: of the property (<paramref name="PropertyOwnerId"/> <c>null</c>) or of one of its owners.</summary>
public sealed record OwnerStayRuleInput(
    Guid PropertyId,
    Guid? PropertyOwnerId,
    int? YearlyNightAllowance,
    int MinAdvanceDays,
    int? MaxAdvanceDays,
    int MinNights,
    int? MaxNights,
    bool ApprovalRequired,
    DateOnly? ActiveFrom,
    DateOnly? ActiveUntil,
    IReadOnlyList<OwnerStayBlackout>? Blackouts);

/// <summary>The rule a request follows for the owner, with the nights the owner holds in <paramref name="Year"/> (requested and approved).</summary>
/// <param name="NightsRemaining">What the allowance leaves this year; <c>null</c> without an allowance.</param>
public sealed record OwnerStayRuleSummary(OwnerStayRuleView Rule, int Year, int NightsUsed, int? NightsRemaining);

/// <summary>What one run of the background work did.</summary>
public sealed record OwnerStayReconcileResult(int Approved, int Released, int SyncChanges, int TodosRaised, int TodosClosed);
