using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>What a stay alert tells the host (one email plus one push each).</summary>
public enum StayAlertKind
{
    /// <summary>Guest data for the Alloggiati communication still missing, the day before arrival.</summary>
    GuestDataMissing,

    /// <summary>Alloggiati communication to send: the deadline is approaching.</summary>
    AlloggiatiDeadlineApproaching,

    /// <summary>Alloggiati deadline passed without the communication (first alert, then the daily reminders).</summary>
    AlloggiatiOverdue,

    /// <summary>Alloggiati communication rejected by the portal or failed.</summary>
    AlloggiatiFailed,

    /// <summary>Check-out day: complete the check-out.</summary>
    CheckoutReminder,

    // E03/F02/US02/T01 (NF-52, CHK-12): the kinds below are appended, never renumbered.

    /// <summary>The communication was sent, entrusted to the channel manager or declared sent, and no receipt is archived the day after.</summary>
    ReceiptMissing,

    /// <summary>A stay of at most one night (the 6-hour term) that is not entrusted to the channel manager arrives within a few hours.</summary>
    ShortStayAtRisk,

    /// <summary>A stay over 30 nights, which the channel manager does not send by itself, arrives tomorrow.</summary>
    OverThirtyNights,
}

/// <summary>One alert to deliver for a booking.</summary>
/// <param name="BookingId">The stay.</param>
/// <param name="Kind">What to tell.</param>
/// <param name="DeadlineUtc">Legal Alloggiati deadline, when the arrival is registered (otherwise the rule is shown).</param>
/// <param name="ShortStay">Stay of at most one night: 6-hour term instead of 24.</param>
/// <param name="ReminderNumber">1..<paramref name="MaxReminders"/> for the daily overdue reminders, 0 otherwise.</param>
/// <param name="MaxReminders">Configured maximum of daily overdue reminders.</param>
public sealed record StayAlert(
    Guid BookingId,
    StayAlertKind Kind,
    DateTime? DeadlineUtc = null,
    bool ShortStay = false,
    int ReminderNumber = 0,
    int MaxReminders = 0);

/// <summary>Stages of <see cref="StayAlertType.AlloggiatiDeadline"/>: each one is sent at most once per check-in date.</summary>
public static class AlloggiatiAlertStage
{
    public const int GuestDataMissing = 1;
    public const int DeadlineApproaching = 2;
    public const int Overdue = 3;

    /// <summary>Stage of the <paramref name="number"/>-th daily reminder after <see cref="Overdue"/> (1-based).</summary>
    public static int OverdueReminder(int number) => Overdue + number;
}

/// <summary>Stage due now for a stay, with the alert to send when the stage is claimed.</summary>
public sealed record StayAlertStep(StayAlertType Type, DateTime ReferenceDate, int Stage, StayAlert Alert);

/// <summary>
/// When each stay alert is due (CO-10). Pure rules on UTC instants; the hourly job sends only the highest stage due
/// and only when it is higher than the last stage sent, so a missed run is caught up by the next one with a single
/// message, never with the backlog.
/// </summary>
/// <remarks>
/// Alloggiati Web (art. 109 TULPS, <see cref="AlloggiatiTerms"/>): the communication is due within 24 hours of arrival,
/// 6 for stays of at most one night, and the portal accepts it only from the arrival day (Europe/Rome). Sequence:
/// <list type="number">
/// <item><b>Guest data missing</b>: the day before arrival at <see cref="StayAlertOptions.GuestDataReminderHourLocal"/>,
/// only while the data of the guests of the stay are incomplete.</item>
/// <item><b>Deadline approaching</b>: <see cref="StayAlertOptions.DeadlineWarningHours"/> before the alert deadline, never
/// before the start of the arrival day.</item>
/// <item><b>Overdue</b>: at the alert deadline.</item>
/// <item><b>Daily reminders</b>: at most <see cref="StayAlertOptions.MaxOverdueReminders"/>, at
/// <see cref="StayAlertOptions.OverdueReminderHourLocal"/> of the following days.</item>
/// </list>
/// The alert deadline is the legal one (<see cref="AlloggiatiTerms.DeadlineUtc(Booking)"/>) once the arrival is
/// registered. Without the arrival time it is the end of the arrival day: for a stay of 24 hours or more that is the
/// deadline CasaZen shows anyway; for a short stay the deadline CasaZen shows (6 hours after the start of the day) is
/// before any real arrival, and an "overdue" alert at that time would almost always be false.
/// </remarks>
public static class StayAlertSchedule
{
    /// <summary>A check-out reminder not sent within this time of its hour (job down) is dropped.</summary>
    public static readonly TimeSpan CheckoutReminderLateWindow = TimeSpan.FromHours(12);

    /// <summary>How long the last stage of the Alloggiati sequence stays due after its time.</summary>
    public static readonly TimeSpan LastStageValidity = TimeSpan.FromHours(24);

    /// <summary>
    /// The only stage of <see cref="StayAlertType.ReceiptMissing"/>, <see cref="StayAlertType.ShortStayAtRisk"/> and
    /// <see cref="StayAlertType.OverThirtyNights"/> (E03/F02/US02/T01): each is sent once for a check-in date.
    /// </summary>
    public const int SingleStage = 1;

    /// <summary>
    /// How long the single stage of the receipt and of the over-30-nights alerts stays due after its time: a stage that is due only because the
    /// job was down is dropped after it, never sent late as a backlog.
    /// </summary>
    public static readonly TimeSpan SingleStageValidity = TimeSpan.FromHours(24);

    /// <summary>
    /// Highest Alloggiati deadline stage due at <paramref name="nowUtc"/> for a stay whose communication is not sent,
    /// or null when none is due yet or the last stage is more than <see cref="LastStageValidity"/> old.
    /// </summary>
    public static StayAlertStep? DueAlloggiatiStep(
        Booking booking,
        bool guestDataComplete,
        StayAlertOptions options,
        DateTime nowUtc)
    {
        var shortStay = AlloggiatiTerms.IsShortStay(booking.CheckInDate, booking.CheckOutDate);
        var legalDeadline = booking.ArrivedAt is null ? (DateTime?)null : AlloggiatiTerms.DeadlineUtc(booking);
        var alertDeadline = AlertDeadlineUtc(booking);
        var referenceDate = booking.CheckInDate.Date;

        StayAlertStep Step(int stage, StayAlertKind kind, int reminder = 0) => new(
            StayAlertType.AlloggiatiDeadline,
            referenceDate,
            stage,
            new StayAlert(booking.Id, kind, legalDeadline, shortStay, reminder, options.MaxOverdueReminders));

        // The last stage of the sequence is dropped a day after its time: a stay that enters the job's window late
        // (outage, first deploy) gets no stale message.
        var reminders = Math.Max(0, options.MaxOverdueReminders);
        for (var reminder = reminders; reminder >= 1; reminder--)
        {
            var reminderAt = OverdueReminderAtUtc(alertDeadline, reminder, options.OverdueReminderHourLocal);
            if (nowUtc >= reminderAt)
            {
                return reminder == reminders && nowUtc >= reminderAt + LastStageValidity
                    ? null
                    : Step(AlloggiatiAlertStage.OverdueReminder(reminder), StayAlertKind.AlloggiatiOverdue, reminder);
            }
        }

        if (nowUtc >= alertDeadline)
        {
            return reminders == 0 && nowUtc >= alertDeadline + LastStageValidity
                ? null
                : Step(AlloggiatiAlertStage.Overdue, StayAlertKind.AlloggiatiOverdue);
        }

        if (nowUtc >= DeadlineWarningAtUtc(booking.CheckInDate, alertDeadline, options.DeadlineWarningHours))
            return Step(AlloggiatiAlertStage.DeadlineApproaching, StayAlertKind.AlloggiatiDeadlineApproaching);

        if (!guestDataComplete && nowUtc >= GuestDataReminderAtUtc(booking.CheckInDate, options.GuestDataReminderHourLocal))
            return Step(AlloggiatiAlertStage.GuestDataMissing, StayAlertKind.GuestDataMissing);

        return null;
    }

    /// <summary>
    /// The «receipt missing» stage (E03/F02/US02/T01, CHK-12): the communication was sent, entrusted to the channel manager or declared sent at
    /// <paramref name="communicatedAtUtc"/> and no receipt is archived (the caller says so: the compliance status engine decides it, never a
    /// copy of its rule). Due the day after the communication (Europe/Rome) at <see cref="StayAlertOptions.ReceiptMissingHourLocal"/>, and
    /// dropped <see cref="SingleStageValidity"/> later: a job that was down never sends it as a backlog. For an entrusted stay the caller passes
    /// the legal deadline as the instant of the communication, so the stage is due the day after the deadline.
    /// </summary>
    public static StayAlertStep? DueReceiptMissingStep(
        Booking booking,
        DateTime communicatedAtUtc,
        StayAlertOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(options);

        var communicatedOn = RomeCalendar.TodayAt(new DateTimeOffset(AsUtc(communicatedAtUtc)));
        var dueAt = RomeAt(communicatedOn.AddDays(1), options.ReceiptMissingHourLocal);
        return IsDueOnce(nowUtc, dueAt)
            ? SingleStageStep(booking, StayAlertType.ReceiptMissing, StayAlertKind.ReceiptMissing, deadlineUtc: null, shortStay: false)
            : null;
    }

    /// <summary>
    /// The «short stay at risk» stage (E03/F02/US02/T01, CHK-12): a stay of at most one night has the 6-hour term, and the daily run of the
    /// channel manager does not cover it unless its guests were entrusted before the arrival. When the stay is not
    /// <paramref name="entrusted"/> it is due from <see cref="StayAlertOptions.ShortStayRiskHours"/> before the arrival (the registered one, or
    /// the start of the arrival day) until the legal deadline, which the overdue stage takes over: after it, nothing.
    /// </summary>
    public static StayAlertStep? DueShortStayRiskStep(
        Booking booking,
        bool entrusted,
        StayAlertOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(options);

        if (entrusted || !AlloggiatiTerms.IsShortStay(booking.CheckInDate, booking.CheckOutDate))
            return null;

        var riskFrom = AlloggiatiTerms.ArrivalInstantUtc(booking.ArrivedAt, booking.CheckInDate)
            .AddHours(-Math.Max(0, options.ShortStayRiskHours));
        var deadline = AlloggiatiTerms.DeadlineUtc(booking);
        return nowUtc >= riskFrom && nowUtc < deadline
            ? SingleStageStep(
                booking,
                StayAlertType.ShortStayAtRisk,
                StayAlertKind.ShortStayAtRisk,
                booking.ArrivedAt is null ? null : deadline,
                shortStay: true)
            : null;
    }

    /// <summary>
    /// The «over 30 nights» stage (E03/F02/US02/T01, CHK-12): the portal takes at most <see cref="AlloggiatiTerms.MaxStayDaysPerSchedina"/> days
    /// on one record and the channel manager does not send a longer stay by itself. One alert the day before the arrival at
    /// <see cref="StayAlertOptions.OverThirtyNightsHourLocal"/> (Europe/Rome), dropped <see cref="SingleStageValidity"/> later.
    /// </summary>
    public static StayAlertStep? DueOverThirtyNightsStep(Booking booking, StayAlertOptions options, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(options);

        if (AlloggiatiTerms.StayDays(booking.CheckInDate, booking.CheckOutDate) <= AlloggiatiTerms.MaxStayDaysPerSchedina)
            return null;

        var dueAt = RomeAt(booking.CheckInDate.Date.AddDays(-1), options.OverThirtyNightsHourLocal);
        return IsDueOnce(nowUtc, dueAt)
            ? SingleStageStep(
                booking,
                StayAlertType.OverThirtyNights,
                StayAlertKind.OverThirtyNights,
                booking.ArrivedAt is null ? null : AlloggiatiTerms.DeadlineUtc(booking),
                shortStay: false)
            : null;
    }

    private static StayAlertStep SingleStageStep(Booking booking, StayAlertType type, StayAlertKind kind, DateTime? deadlineUtc, bool shortStay) =>
        new(type, booking.CheckInDate.Date, SingleStage, new StayAlert(booking.Id, kind, deadlineUtc, shortStay));

    /// <summary>From <paramref name="dueAt"/> until <see cref="SingleStageValidity"/> later.</summary>
    private static bool IsDueOnce(DateTime nowUtc, DateTime dueAt) => nowUtc >= dueAt && nowUtc < dueAt + SingleStageValidity;

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    /// <summary>
    /// The deadline the alerts use: the legal one when the arrival is registered, otherwise the end of the arrival day
    /// in Europe/Rome (see the remarks of <see cref="StayAlertSchedule"/>).
    /// </summary>
    public static DateTime AlertDeadlineUtc(Booking booking) =>
        booking.ArrivedAt is null
            ? RomeCalendar.StartOfDayUtc(booking.CheckInDate.Date.AddDays(1))
            : AlloggiatiTerms.DeadlineUtc(booking);

    /// <summary>The day before the check-in date at <paramref name="hourLocal"/>, Europe/Rome, as UTC.</summary>
    public static DateTime GuestDataReminderAtUtc(DateTime checkInDate, int hourLocal) =>
        RomeAt(checkInDate.Date.AddDays(-1), hourLocal);

    /// <summary><paramref name="warningHours"/> before the alert deadline, never before the arrival day starts (Europe/Rome).</summary>
    public static DateTime DeadlineWarningAtUtc(DateTime checkInDate, DateTime alertDeadlineUtc, int warningHours)
    {
        var warning = alertDeadlineUtc.AddHours(-Math.Max(0, warningHours));
        var arrivalDayStart = AlloggiatiTerms.ArrivalDayStartUtc(checkInDate);
        return warning < arrivalDayStart ? arrivalDayStart : warning;
    }

    /// <summary>
    /// The <paramref name="number"/>-th daily reminder: <paramref name="hourLocal"/> (Europe/Rome) of the
    /// <paramref name="number"/>-th day after the day of the alert deadline.
    /// </summary>
    public static DateTime OverdueReminderAtUtc(DateTime alertDeadlineUtc, int number, int hourLocal)
    {
        var deadlineDay = RomeCalendar.TodayAt(new DateTimeOffset(DateTime.SpecifyKind(alertDeadlineUtc, DateTimeKind.Utc)));
        return RomeAt(deadlineDay.AddDays(number), hourLocal);
    }

    /// <summary>
    /// The check-out day at <paramref name="hourLocal"/> in the property's time zone (Europe/Rome when it has none or
    /// an unknown one), as UTC.
    /// </summary>
    public static DateTime CheckoutReminderAtUtc(DateTime checkOutDate, string? propertyTimeZone, int hourLocal) =>
        LocalToUtc(checkOutDate, hourLocal, ResolveZone(propertyTimeZone));

    /// <summary>
    /// The check-out reminder of a stay confirmed or checked in, due at <paramref name="nowUtc"/>: from its hour until
    /// <see cref="CheckoutReminderLateWindow"/> later. Null otherwise.
    /// </summary>
    public static StayAlertStep? DueCheckoutReminder(Booking booking, string? propertyTimeZone, int hourLocal, DateTime nowUtc)
    {
        if (booking.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn))
            return null;

        var dueAt = CheckoutReminderAtUtc(booking.CheckOutDate, propertyTimeZone, hourLocal);
        if (nowUtc < dueAt || nowUtc >= dueAt + CheckoutReminderLateWindow)
            return null;

        return new StayAlertStep(
            StayAlertType.CheckoutReminder,
            booking.CheckOutDate.Date,
            1,
            new StayAlert(booking.Id, StayAlertKind.CheckoutReminder));
    }

    private static DateTime RomeAt(DateTime calendarDate, int hourLocal) =>
        LocalToUtc(calendarDate, hourLocal, RomeCalendar.TimeZone);

    private static DateTime LocalToUtc(DateTime calendarDate, int hourLocal, TimeZoneInfo zone)
    {
        var local = new DateTime(
            calendarDate.Year, calendarDate.Month, calendarDate.Day, Math.Clamp(hourLocal, 0, 23), 0, 0, DateTimeKind.Unspecified);
        // A wall-clock time skipped by the daylight saving change: one hour later.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static TimeZoneInfo ResolveZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return RomeCalendar.TimeZone;
    }
}

/// <summary>Outcome of one run of <see cref="IStayAlertService.RunAsync"/>.</summary>
/// <param name="Skipped">Another run held the lock: this one did nothing.</param>
/// <param name="Sent">Alerts delivered (each one email plus one push).</param>
public sealed record StayAlertRunResult(bool Skipped, int Sent);

/// <summary>
/// Host alerts about stays, sent by the hourly <c>stay-alerts</c> job (CO-10): the Alloggiati Web sequence of
/// <see cref="StayAlertSchedule"/>, the failed communication and the check-out day reminder. Each stage is sent at most
/// once per stay (<see cref="StayAlertState"/>), whatever the number of runs.
/// </summary>
public interface IStayAlertService
{
    /// <summary>
    /// Sends every alert due now. Only one run at a time: a concurrent run returns <see cref="StayAlertRunResult.Skipped"/>.
    /// </summary>
    Task<StayAlertRunResult> RunAsync(CancellationToken cancellationToken = default);
}
