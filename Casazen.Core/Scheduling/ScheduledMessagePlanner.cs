using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Core.Scheduling;

/// <summary>A routine as the planner reads it: what <c>Routine</c> holds, without the entity (the planner is pure).</summary>
/// <param name="Id">The routine.</param>
/// <param name="Name">Its name (a snapshot for the screen).</param>
/// <param name="EventType">What starts it.</param>
/// <param name="EventConfig">The offset of a time trigger: <c>{ "days": N, "hours": N }</c>.</param>
/// <param name="ActionsJson">The actions, as the engine reads them.</param>
/// <param name="Active">Only an active routine plans anything.</param>
/// <param name="TestMode">A routine in test mode simulates: it plans nothing.</param>
/// <param name="AgentObjective">The objective the host wrote for the agent of the routine (<c>Routine.AgentObjective</c>): an instruction the AI judges against the stay when the routine fires, so a routine that has one is the engine's.</param>
public sealed record PlanningRoutine(Guid Id, string Name, RoutineEventType EventType, string EventConfig, string ActionsJson, bool Active, bool TestMode, string? AgentObjective = null);

/// <summary>The facts of a booking the planner needs: the stay, the moment it was made or confirmed, and the clock of its property.</summary>
/// <param name="CheckInDate">The check-in date as stored (a calendar date, midnight UTC).</param>
/// <param name="CheckOutDate">The check-out date as stored.</param>
/// <param name="CreatedAt">When the booking was made (UTC): the moment of <c>BookingNew</c>.</param>
/// <param name="ConfirmedAt">When it was confirmed (UTC): the moment of <c>BookingConfirmed</c>; <c>null</c> falls back to <paramref name="CreatedAt"/>.</param>
/// <param name="CheckInFrom">The earliest arrival time on the clock of the property; <c>null</c> = the start of the day, as the time triggers count it.</param>
/// <param name="CheckOutBy">The time guests leave by, on the clock of the property; <c>null</c> = the start of the day, as the time triggers count it.</param>
/// <param name="Timezone">The IANA zone of the property.</param>
public sealed record PlanningBooking(
    Guid Id,
    BookingStatus Status,
    bool NoShow,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    TimeOnly? CheckInFrom,
    TimeOnly? CheckOutBy,
    string? Timezone);

public sealed record PlannedMessage(int ActionIndex, string TemplateRef, DateTime PlannedAtUtc, bool IsCampaign = false);

/// <summary>
/// The messages the active routines will send for a booking, and when (E05/F07/US01/T02, NF-71): pure, no database and no clock.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which routines.</b> An active routine that is not in test mode, on a stay that happens: <c>Confirmed</c>, <c>CheckedIn</c> or <c>CheckedOut</c> and not a no-show (the definition
/// of the time triggers). The events that cannot be foreseen (a payment, a message, an activity, a recurrence) plan nothing: they send when they fire (the dispatch writes a row after the fact).</item>
/// <item><b>When.</b> The same moment the time trigger fires at (<c>TimeEventEmitterJob</c>): the date of the check-in or of the check-out with the time of the property on its own clock
/// (<c>CheckInFrom</c>, <c>CheckOutBy</c>; the start of the day when it has none), converted with <see cref="RomeCalendar.ToUtc(DateOnly, TimeOnly, TimeZoneInfo)"/> (the two days a year the clock
/// changes have a rule), moved by the offset of the routine in whole hours, so what the host sees is the moment the routine fires. For <c>BookingNew</c> and <c>BookingConfirmed</c> it is the
/// moment of the event. Every <c>Wait</c> before the message adds its delay.</item>
/// <item><b>Never outside the send window.</b> A moment before the window waits for it, one after it waits for the next morning (<see cref="IntoWindow"/>): a message is never planned for the middle
/// of the night. What goes out outside the window anyway (a late sweep, «Send now») is flagged by the dispatch.</item>
/// <item><b>What the engine decides is not planned.</b> An action with an <c>aiCondition</c> and every action of a routine that has an agent objective is judged by the AI against the stay when the routine
/// fires: the plan neither foresees nor evaluates it (<see cref="IsEngineOwned"/>). Its row is written when the routine fires and the engine let it through, like the one of an event that cannot be foreseen.
/// A campaign (<c>isCampaign</c>) is planned, with <see cref="PlannedMessage.IsCampaign"/> set, because the consent it needs is the guest's and is read where the message is planned and again where it is sent.</item>
/// </list>
/// </remarks>
public static class ScheduledMessagePlanner
{
    public const int MaxOffsetHours = 366 * 24;

    public static bool IsPlannable(RoutineEventType eventType) =>
        eventType is RoutineEventType.BeforeCheckin
            or RoutineEventType.AfterCheckin
            or RoutineEventType.BeforeCheckout
            or RoutineEventType.AfterCheckout
            or RoutineEventType.BookingNew
            or RoutineEventType.BookingConfirmed;

    public static IReadOnlyList<PlannedMessage> Plan(PlanningRoutine routine, PlanningBooking booking, ScheduledMessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(options);

        if (!routine.Active || routine.TestMode || !IsPlannable(routine.EventType) || !HappensStay(booking))
            return [];

        if (HasAgentObjective(routine))
            return [];

        if (!RoutineAction.TryParseList(routine.ActionsJson, out var actions))
            return [];

        var baseMoment = BaseMomentOf(routine, booking);
        if (baseMoment is not { } moment)
            return [];

        var zone = RomeCalendar.ZoneOf(booking.Timezone);
        var (from, to) = options.EffectiveSendWindow;
        var waited = TimeSpan.Zero;
        var planned = new List<PlannedMessage>();
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (action.Type == RoutineActionType.Wait)
            {
                if (RoutineActionRules.WaitDelay(action) is not { } delay)
                    break;

                waited += delay;
                continue;
            }

            if (action.Type != RoutineActionType.MessageToGuest || !RoutineActionRules.HasRequiredFields(action))
                continue;

            if (HasAiCondition(action))
                continue;

            var templateRef = action.GetString("templateId")!.Trim();
            planned.Add(new PlannedMessage(index, templateRef, IntoWindow(moment + waited, zone, from, to), action.IsOnUnlessFalse(CampaignMarker)));
        }

        return planned;
    }

    public const string CampaignMarker = "isCampaign";

    public const string AiConditionMember = "aiCondition";

    private static bool HasAgentObjective(PlanningRoutine routine) => !string.IsNullOrWhiteSpace(routine.AgentObjective);

    public static bool HasAiCondition(RoutineAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Config.ContainsKey(AiConditionMember);
    }

    /// <summary>
    /// Whether the <c>MessageToGuest</c> of the routine at <paramref name="actionIndex"/> is one the engine decides and the plan does not foresee: a routine on an event that cannot be foreseen, one with an
    /// objective for its agent, or an action with an <c>aiCondition</c>. Such a row is written when the routine fires and the engine let it through, and the plan leaves it alone (it never closes it for
    /// being «no longer planned»). <c>false</c> for a routine that is off, in test mode or no longer holds a message at that place: those rows are the plan's to close.
    /// </summary>
    public static bool IsEngineOwned(PlanningRoutine routine, int actionIndex)
    {
        ArgumentNullException.ThrowIfNull(routine);

        if (!routine.Active || routine.TestMode || !RoutineAction.TryParseList(routine.ActionsJson, out var actions))
            return false;

        if (actionIndex < 0 || actionIndex >= actions.Count)
            return false;

        var action = actions[actionIndex];
        if (action.Type != RoutineActionType.MessageToGuest || !RoutineActionRules.HasRequiredFields(action))
            return false;

        return !IsPlannable(routine.EventType) || HasAgentObjective(routine) || HasAiCondition(action);
    }

    public static int? OffsetHours(string? eventConfig)
    {
        if (string.IsNullOrWhiteSpace(eventConfig))
            return 0;

        try
        {
            using var document = JsonDocument.Parse(eventConfig);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            long days = 0;
            long hours = 0;
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (string.Equals(member.Name, "days", StringComparison.OrdinalIgnoreCase) && !TryWholeNumber(member.Value, out days))
                    return null;
                if (string.Equals(member.Name, "hours", StringComparison.OrdinalIgnoreCase) && !TryWholeNumber(member.Value, out hours))
                    return null;
            }

            var total = (days * 24) + hours;
            return total <= MaxOffsetHours ? (int)total : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DateTime IntoWindow(DateTime utc, TimeZoneInfo zone, TimeOnly from, TimeOnly to)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var time = TimeOnly.FromDateTime(local);
        if (time >= from && time <= to)
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        var day = DateOnly.FromDateTime(local);
        return RomeCalendar.ToUtc(time < from ? day : day.AddDays(1), from, zone);
    }

    public static bool IsInsideWindow(DateTime utc, TimeZoneInfo zone, TimeOnly from, TimeOnly to)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var time = TimeOnly.FromDateTime(local);
        return time >= from && time <= to;
    }

    /// <summary>
    /// A stay that happens: one of the active statuses (confirmed, checked in or checked out: a check-out comes with its messages after it) and not a no-show, the one definition of
    /// <see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> that the time triggers, the Alloggiati job and the dispatch use.
    /// </summary>
    private static bool HappensStay(PlanningBooking booking) =>
        !booking.NoShow && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status);

    private static DateTime? BaseMomentOf(PlanningRoutine routine, PlanningBooking booking)
    {
        switch (routine.EventType)
        {
            case RoutineEventType.BookingNew:
                return DateTime.SpecifyKind(booking.CreatedAt, DateTimeKind.Utc);
            case RoutineEventType.BookingConfirmed:
                return DateTime.SpecifyKind(booking.ConfirmedAt ?? booking.CreatedAt, DateTimeKind.Utc);
        }

        if (OffsetHours(routine.EventConfig) is not { } offset)
            return null;

        var isCheckIn = routine.EventType is RoutineEventType.BeforeCheckin or RoutineEventType.AfterCheckin;
        var isBefore = routine.EventType is RoutineEventType.BeforeCheckin or RoutineEventType.BeforeCheckout;
        var date = DateOnly.FromDateTime(isCheckIn ? booking.CheckInDate : booking.CheckOutDate);
        var time = (isCheckIn ? booking.CheckInFrom : booking.CheckOutBy) ?? TimeOnly.MinValue;
        var zone = RomeCalendar.ZoneOf(booking.Timezone);

        return RomeCalendar.ToUtc(date, time, zone).AddHours(isBefore ? -offset : offset);
    }

    private static bool TryWholeNumber(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number
               && value.TryGetInt64(out number)
               && number is >= 0 and <= MaxOffsetHours;
    }
}
