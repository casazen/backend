using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The one write path of the calendar workspace (E08/F05/US02/T03, NF-36, NF-78): a change of price and rules for a period of one or many properties,
/// previewed with a summary and applied all or nothing, with the push to the channel manager recorded as a process. The day panel (one property, a few
/// days) and the bulk wizard (many properties, a long period) both use it.
/// </summary>
/// <remarks>
/// <para><b>Reach.</b> The services never decide on roles, but every property of a request is read through the <see cref="HostScope"/> of the caller: one
/// the caller does not reach (another org's, or one a collaborator limited to some properties was not given) does not exist for the request. The web layer
/// authorizes the permission on every property before it calls.</para>
/// <para><b>All or nothing.</b> <see cref="ApplyAsync"/> stages the rules of every property, then the process and its chunks, and saves them in one
/// transaction: a property that fails validation fails the whole request and nothing is saved. After the commit a process that is <c>Queued</c> is enqueued once
/// for an immediate run; the recurring job is the safety net.</para>
/// </remarks>
public interface ICalendarChangeService
{
    /// <summary>
    /// What <paramref name="request"/> would do, and nothing else: per property how many days change, stay as they are, are taken or are over, the first five
    /// changed days with their values before and after, whether the property is on the channel manager and the quota it would use (an estimate), and warnings with
    /// stable codes. Stages and saves nothing. Refuses what <see cref="ApplyAsync"/> refuses.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">A code of <see cref="DayRuleErrorCodes"/>, <see cref="HostCalendarErrorCodes.TooManyProperties"/> or <see cref="CalendarPushErrorCodes.NoProperties"/> (422).</exception>
    /// <exception cref="Exceptions.NotFoundException">A property does not exist for the caller (404 <c>property_not_found</c>).</exception>
    Task<CalendarChangePreview> PreviewAsync(HostScope scope, CalendarChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="request"/> to every property, in one transaction, and records the process of the push (<c>DayRules</c> for one property,
    /// <c>Bulk</c> for several). A request that changes nothing is refused (422 <c>calendar_day_rule_nothing_to_change</c>), and so is a range that is over for
    /// every property (422 <c>calendar_day_rule_in_past</c>); a property that fails validation fails the whole request and nothing is saved.
    /// </summary>
    /// <param name="userId">The Auth0 subject of the account that changes the calendar; <c>null</c> for a change of the system.</param>
    /// <exception cref="Exceptions.DomainRuleException">As for <see cref="PreviewAsync"/>.</exception>
    /// <exception cref="Exceptions.NotFoundException">A property does not exist for the caller (404 <c>property_not_found</c>).</exception>
    Task<CalendarChangeApplyResult> ApplyAsync(
        HostScope scope,
        CalendarChangeRequest request,
        string? userId,
        CancellationToken cancellationToken = default);
}

/// <summary>A change of the price and the rules of the nights of one or many properties.</summary>
/// <param name="PropertyIds">The properties: 1 to 50, without repeats; never «all» when empty.</param>
/// <param name="From">The first day, included.</param>
/// <param name="To">The last day, included: at most 366 days from <paramref name="From"/>.</param>
/// <param name="DaysOfWeek">Only the nights of these weekdays; <c>null</c> for every day; an empty set is refused, never read as «all».</param>
/// <param name="Patch">What to do to each value of the rules of the nights (keep, set or clear).</param>
public sealed record CalendarChangeRequest(
    IReadOnlyList<Guid> PropertyIds,
    DateOnly From,
    DateOnly To,
    IReadOnlySet<DayOfWeek>? DaysOfWeek,
    DayRulePatch Patch);

/// <summary>What a night holds as the guest and the channel manager see it (the effective values, not the rule alone).</summary>
/// <param name="PriceCents">What the night costs, in cents of the currency of the property: <see cref="StayPricing.NightPrice"/>.</param>
/// <param name="MinNights">The fewest nights of a stay that starts on the night: the rule, else the minimum of the property, else 1.</param>
/// <param name="MaxNights">The most nights; <c>null</c> for no maximum.</param>
/// <param name="ClosedToArrival">A stay cannot start on the night.</param>
/// <param name="ClosedToDeparture">A stay cannot end on the day.</param>
/// <param name="MinAdvanceDays">The fewest days ahead a stay may be booked; <c>null</c> for no minimum.</param>
/// <param name="MaxAdvanceDays">The most days ahead a stay may be booked; <c>null</c> for no maximum.</param>
public sealed record CalendarChangeValues(
    long PriceCents,
    int MinNights,
    int? MaxNights,
    bool ClosedToArrival,
    bool ClosedToDeparture,
    int? MinAdvanceDays,
    int? MaxAdvanceDays);

/// <summary>A night the change would change, with its values before and after.</summary>
/// <param name="Date">The night.</param>
/// <param name="Before">What the night holds now.</param>
/// <param name="After">What it would hold after the change.</param>
public sealed record CalendarChangeSample(DateOnly Date, CalendarChangeValues Before, CalendarChangeValues After);

/// <summary>What the change would do to the nights of one property.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Name">The name of the property.</param>
/// <param name="Mirrored">The property books into the channel manager: the change would be sent there.</param>
/// <param name="Days">The nights the change selects (the weekdays chosen): changed, unchanged and over together.</param>
/// <param name="ChangedDays">The nights whose rule would change.</param>
/// <param name="UnchangedDays">The nights the change leaves as they are.</param>
/// <param name="TakenDays">The nights, among the changed and the unchanged, that a booking, a hold, a request or a block takes: counted, not skipped (a rule on a booked night changes nothing for that booking).</param>
/// <param name="SkippedPastDays">The nights before today of the property, left alone.</param>
/// <param name="Samples">The first five changed nights, with their values before and after.</param>
/// <param name="EstimatedQuotaUnits">The units of quota of the channel manager the change would use (an estimate; 0 when nothing is sent).</param>
public sealed record CalendarChangePreviewProperty(
    Guid PropertyId,
    string Name,
    bool Mirrored,
    int Days,
    int ChangedDays,
    int UnchangedDays,
    int TakenDays,
    int SkippedPastDays,
    IReadOnlyList<CalendarChangeSample> Samples,
    int EstimatedQuotaUnits);

/// <summary>Something the host should know before it applies a change.</summary>
/// <param name="Code">A stable code of <see cref="CalendarPushErrorCodes"/> (<c>property_not_mirrored</c>, <c>room_not_chosen</c>).</param>
/// <param name="PropertyId">The property it is about.</param>
public sealed record CalendarChangeWarning(string Code, Guid PropertyId);

/// <summary>The preview of a change: per property and in total, with the warnings. The quota is an estimate.</summary>
public sealed record CalendarChangePreview(
    IReadOnlyList<CalendarChangePreviewProperty> Properties,
    int Days,
    int ChangedDays,
    int UnchangedDays,
    int TakenDays,
    int SkippedPastDays,
    int EstimatedQuotaUnits,
    IReadOnlyList<CalendarChangeWarning> Warnings);

/// <summary>What was applied: the process that follows the change and the counts.</summary>
/// <param name="ProcessId">The process recorded for the change (the one of <c>GET /api/calendar/changes/{id}</c>).</param>
/// <param name="State">Its state: <c>Queued</c> when something is sent, <c>Applied</c> when the change stays in CasaZen.</param>
/// <param name="ChangedDays">The nights whose rule changed, all the properties together.</param>
/// <param name="UnchangedDays">The nights left as they were.</param>
/// <param name="SkippedPastDays">The nights before today of their property, left alone.</param>
public sealed record CalendarChangeApplyResult(
    Guid ProcessId,
    CalendarChangeState State,
    int ChangedDays,
    int UnchangedDays,
    int SkippedPastDays);
