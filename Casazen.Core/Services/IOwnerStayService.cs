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
    Task<OwnerCalendar> GetCalendarAsync(
        OwnerPortalAccess access,
        Guid propertyId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerStayView>> ListForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    Task<OwnerStayRuleSummary> GetRuleForOwnerAsync(OwnerPortalAccess access, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for an owner stay. Needs <c>stays.request</c> on the property. Stored as <see cref="OwnerStayStatus.Requested"/>; when the rule does not ask for a
    /// person, CasaZen approves it in the background (the block is created there, never by the request).
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">A code of <see cref="OwnerStayErrors"/> for a rule the request breaks.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="OwnerStayErrors.DatesUnavailable"/>.</exception>
    Task<OwnerStayView> RequestAsync(OwnerPortalAccess access, OwnerStayRequestCommand command, CancellationToken cancellationToken = default);

    Task<OwnerStayView> CancelByOwnerAsync(OwnerPortalAccess access, Guid stayId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerStayAdminView>> ListForAgencyAsync(
        Guid orgId,
        OwnerStayStatus? status,
        Guid? propertyId,
        CancellationToken cancellationToken = default);

    Task<OwnerStayAdminView> ApproveAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    Task<OwnerStayAdminView> RejectAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    Task<OwnerStayAdminView> CancelByAgencyAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends again the block (or its release) the channel manager refused. Idempotent: a stay with nothing failed answers as it is and sends nothing.
    /// </summary>
    Task<OwnerStayAdminView> RetrySyncAsync(Guid orgId, Guid stayId, string actorUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerStayRuleView>> ListRulesAsync(Guid orgId, Guid? propertyId, CancellationToken cancellationToken = default);

    Task<OwnerStayRuleView> SaveRuleAsync(Guid orgId, OwnerStayRuleInput input, string actorUserId, CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(Guid orgId, Guid ruleId, string actorUserId, CancellationToken cancellationToken = default);

    Task<OwnerStayReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default);

    Task ProcessAsync(Guid stayId, CancellationToken cancellationToken = default);
}

public static class OwnerStayErrors
{
    public const string NotFound = "owner_stay_not_found";

    public const string InvalidRange = "owner_stay_invalid_range";

    public const string InPast = "owner_stay_in_past";

    public const string TooShort = "owner_stay_too_short";

    public const string TooLong = "owner_stay_too_long";

    public const string NoticeTooShort = "owner_stay_notice_too_short";

    public const string BeyondWindow = "owner_stay_beyond_window";

    public const string Blackout = "owner_stay_blackout";

    public const string OutsidePeriod = "owner_stay_outside_period";

    public const string AllowanceExceeded = "owner_stay_allowance_exceeded";

    public const string DatesUnavailable = "owner_stay_dates_unavailable";

    public const string NotPending = "owner_stay_not_pending";

    public const string NotCancellable = "owner_stay_not_cancellable";

    public const string CalendarRangeInvalid = "owner_calendar_range_invalid";

    public const string RuleInvalid = "owner_stay_rule_invalid";

    public const string RuleNotFound = "owner_stay_rule_not_found";

    public const string PropertyNotFound = "owner_stay_property_not_found";

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

public static class OwnerCalendarKinds
{
    public const string Available = "available";

    public const string Occupied = "occupied";

    public const string Blocked = "blocked";

    public const string OwnerStay = "ownerStay";
}

public static class OwnerStayStates
{
    public const string Requested = "requested";

    public const string ApprovedSyncing = "approved_syncing";

    public const string Confirmed = "confirmed";

    public const string SyncFailed = "sync_failed";

    public const string Rejected = "rejected";

    public const string Cancelled = "cancelled";
}

public sealed record OwnerStayRequestCommand(Guid PropertyId, DateOnly StartDate, DateOnly EndDate);

/// <summary>A run of days of the owner calendar with the same kind.</summary>
/// <param name="Start">The first night.</param>
/// <param name="End">The day after the last night (excluded), as a stay counts it.</param>
/// <param name="Kind">One of <see cref="OwnerCalendarKinds"/>.</param>
/// <param name="StayId">The stay, for <see cref="OwnerCalendarKinds.OwnerStay"/> only.</param>
/// <param name="StayState">Its state (<see cref="OwnerStayStates"/>), for <see cref="OwnerCalendarKinds.OwnerStay"/> only.</param>
public sealed record OwnerCalendarRange(DateOnly Start, DateOnly End, string Kind, Guid? StayId = null, string? StayState = null);

public sealed record OwnerCalendar(Guid PropertyId, DateOnly From, DateOnly To, IReadOnlyList<OwnerCalendarRange> Ranges);

public sealed record OwnerStayBlackout(DateOnly From, DateOnly To);

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

public sealed record OwnerStayAdminView(OwnerStayView Stay, string OwnerName, bool AutoApprove);

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

public sealed record OwnerStayRuleSummary(OwnerStayRuleView Rule, int Year, int NightsUsed, int? NightsRemaining);

public sealed record OwnerStayReconcileResult(int Approved, int Released, int SyncChanges, int TodosRaised, int TodosClosed);
