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
public sealed record PlanningRoutine(Guid Id, string Name, RoutineEventType EventType, string EventConfig, string ActionsJson, bool Active, bool TestMode);

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

/// <summary>One message a routine will send for a booking: the action, the template it names and when it goes out.</summary>
/// <param name="ActionIndex">The position of the <c>MessageToGuest</c> action in the routine.</param>
/// <param name="TemplateRef">The id of the template the action names.</param>
/// <param name="PlannedAtUtc">When it goes out (UTC), inside the send window of the property.</param>
public sealed record PlannedMessage(int ActionIndex, string TemplateRef, DateTime PlannedAtUtc);

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
/// </list>
/// </remarks>
public static class ScheduledMessagePlanner
{
    /// <summary>The largest offset taken, one year in hours (the one of <c>TimeTrigger</c>): a longer one is a mistake in the routine, not a trigger.</summary>
    public const int MaxOffsetHours = 366 * 24;

    /// <summary>The routine event types that have a moment the plan can foresee.</summary>
    public static bool IsPlannable(RoutineEventType eventType) =>
        eventType is RoutineEventType.BeforeCheckin
            or RoutineEventType.AfterCheckin
            or RoutineEventType.BeforeCheckout
            or RoutineEventType.AfterCheckout
            or RoutineEventType.BookingNew
            or RoutineEventType.BookingConfirmed;

    /// <summary>
    /// The messages <paramref name="routine"/> sends for <paramref name="booking"/>, each with its moment, in the order of the actions. Nothing for a routine that is not active, is in test mode,
    /// is on an event that cannot be foreseen, has actions that cannot be read, or for a stay that does not happen.
    /// </summary>
    public static IReadOnlyList<PlannedMessage> Plan(PlanningRoutine routine, PlanningBooking booking, ScheduledMessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(options);

        if (!routine.Active || routine.TestMode || !IsPlannable(routine.EventType) || !HappensStay(booking))
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
                // A wait the engine would refuse ends the routine: nothing after it is foreseen.
                if (RoutineActionRules.WaitDelay(action) is not { } delay)
                    break;

                waited += delay;
                continue;
            }

            if (action.Type != RoutineActionType.MessageToGuest || !RoutineActionRules.HasRequiredFields(action))
                continue;

            var templateRef = action.GetString("templateId")!.Trim();
            planned.Add(new PlannedMessage(index, templateRef, IntoWindow(moment + waited, zone, from, to)));
        }

        return planned;
    }

    /// <summary>
    /// The offset a routine asks for, in whole hours: <c>days * 24 + hours</c> of its <c>EventConfig</c>, the one reading of <c>TimeTrigger.OffsetHours</c> (a test keeps them equal); <c>null</c> when
    /// the configuration is not that: not JSON, not an object, a member that is not a whole number of zero or more, or more than <see cref="MaxOffsetHours"/> in all.
    /// </summary>
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

    /// <summary>
    /// <paramref name="utc"/> if it is inside the send window on the clock of <paramref name="zone"/>, else the start of the next window: the same day when it is before the window, the next day when it
    /// is after it. The two days of the year the clock changes keep their rule (<see cref="RomeCalendar.ToUtc(DateOnly, TimeOnly, TimeZoneInfo)"/>).
    /// </summary>
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

    /// <summary>Whether <paramref name="utc"/> is inside the send window on the clock of <paramref name="zone"/>.</summary>
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

    /// <summary>The moment the routine starts from: the time trigger of a stay, or the moment of the event; <c>null</c> when the routine says no valid offset.</summary>
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

        // Exactly what TimeEventEmitterJob computes: the wall-clock time of the property on that date, in UTC, then the offset in whole hours.
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
