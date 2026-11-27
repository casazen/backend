using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// When the alerts about the 24 hours of the notification of the guests of a Ticino property to the cantonal police are due (E30/F02/US01/T01, NF-127, D9).
/// Pure rules on UTC instants, the zone of the property and <see cref="ComplianceOptions"/>, like the period and connection alerts of the same class: the job
/// sends only the highest stage due and only when it is higher than the last one sent, so a missed run is caught up with one message, never with the backlog,
/// and the last stage of the sequence is dropped when it is older than <see cref="LastStageValidity"/>.
/// </summary>
/// <remarks>
/// <para><b>One sequence for each arrival day of a property</b> (<see cref="TicinoReferenceKey"/>), as long as the day is still to notify. The days and the
/// instant they are due (<c>DueAtUtc</c>: 24 hours from the first recorded arrival, else the end of the arrival day) come from
/// <see cref="ITicinoNotificationService.ListPendingDaysAsync"/>; whether a day is closed is not decided here.</para>
/// <list type="number">
/// <item><see cref="ComplianceAlertStage.TicinoFileToPrepare"/>: on the arrival day, <see cref="ComplianceOptions.TicinoArrivalAlertDelayHours"/> after the first
/// expected check-in of the day, or at <see cref="ComplianceOptions.TicinoArrivalAlertDefaultHourLocal"/> when no stay of the day has one, on the clock of the
/// property, and never later than 23:00 of the arrival day.</item>
/// <item><see cref="ComplianceAlertStage.TicinoDeadlineApproaching"/>: <see cref="ComplianceOptions.TicinoDueWarningHours"/> before the deadline.</item>
/// <item><see cref="ComplianceAlertStage.TicinoOverdue"/>: at the deadline; then at most <see cref="ComplianceOptions.TicinoMaxOverdueReminders"/> reminders,
/// <see cref="ComplianceOptions.TicinoOverdueReminderHours"/> apart, until the day is closed.</item>
/// </list>
/// <para><b>Daylight saving.</b> The first stage is a wall-clock time of the property, so it is the instant that wall-clock time is on that day:
/// 18:00 is 17:00 UTC in winter and 16:00 UTC in summer; a time inside the hour the clocks skip in March (02:00-02:59 on the last Sunday) is read one hour later
/// on the clock, and one inside the hour they repeat in October is its first occurrence (<see cref="RomeCalendar.ToUtc(DateOnly, TimeOnly, TimeZoneInfo)"/>).
/// The deadline and the reminders are absolute durations from <c>DueAtUtc</c>. With no recorded arrival the deadline is the end of the arrival day, six hours
/// before it is 18:00 on the clock, which is also the default time of the first stage: that day the two coincide and only the higher stage is sent.</para>
/// </remarks>
public static partial class ComplianceAlertSchedule
{
    /// <summary>The last hour of the arrival day (property clock) at which the first alert of a Ticino day can be due.</summary>
    internal const int TicinoLastArrivalAlertHour = 23;

    /// <summary>
    /// The <see cref="ComplianceAlertState.ReferenceKey"/> of the alerts of an arrival day: <c>yyyy-MM-dd</c>. The property is the other column of the unique index
    /// (property, type, reference), so it is not repeated in the key, which a column of 32 characters could not hold with the id of the property.
    /// </summary>
    public static string TicinoReferenceKey(DateOnly arrivalDay) => arrivalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The earliest expected check-in of the stays of an arrival day: the earliest valid <c>HH:mm</c> of <paramref name="expectedArrivalTimes"/> (the
    /// <c>Booking.ExpectedArrivalTime</c> of the stays). <c>late</c>, a blank value and anything that is not a time say nothing about when: they are ignored.
    /// <c>null</c> when no stay of the day gave one.
    /// </summary>
    public static TimeOnly? TicinoFirstExpectedArrival(IEnumerable<string?> expectedArrivalTimes)
    {
        ArgumentNullException.ThrowIfNull(expectedArrivalTimes);

        TimeOnly? first = null;
        foreach (var value in expectedArrivalTimes)
        {
            if (!ArrivalTimeRules.TryNormalize(value, out var normalized)
                || normalized is null
                || !TimeOnly.TryParseExact(normalized, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            if (first is null || time < first)
                first = time;
        }

        return first;
    }

    /// <summary>
    /// The instant the first alert of the arrival day <paramref name="arrivalDay"/> is due: <see cref="ComplianceOptions.TicinoArrivalAlertDelayHours"/> after
    /// <paramref name="firstExpectedArrival"/> on the clock of <paramref name="propertyZone"/>, or the hour <see cref="ComplianceOptions.TicinoArrivalAlertDefaultHourLocal"/>
    /// when there is none; never later than 23:00 of the arrival day, so the stage of «the arrival day» is not sent in the night after it.
    /// </summary>
    public static DateTime TicinoArrivalAlertAtUtc(DateOnly arrivalDay, TimeZoneInfo propertyZone, TimeOnly? firstExpectedArrival, ComplianceOptions options)
    {
        ArgumentNullException.ThrowIfNull(propertyZone);
        ArgumentNullException.ThrowIfNull(options);

        var latestMinute = TicinoLastArrivalAlertHour * 60;
        var minuteOfDay = firstExpectedArrival is { } expected
            ? Math.Min((expected.Hour * 60) + expected.Minute + (Math.Clamp(options.TicinoArrivalAlertDelayHours, 0, 24) * 60), latestMinute)
            : Math.Clamp(options.TicinoArrivalAlertDefaultHourLocal, 0, TicinoLastArrivalAlertHour) * 60;
        return RomeCalendar.ToUtc(arrivalDay, new TimeOnly(minuteOfDay / 60, minuteOfDay % 60), propertyZone);
    }

    /// <summary>
    /// The highest stage due now for the notification of the arrival day <paramref name="arrivalDay"/> of a Ticino property whose deadline is
    /// <paramref name="dueAtUtc"/>, that nothing closed (the caller read the day from <see cref="ITicinoNotificationService.ListPendingDaysAsync"/>), or <c>null</c>
    /// when none is due yet or the last stage is more than <see cref="LastStageValidity"/> old. The sequence is the one of the remarks of this class.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="arrivalDay">The arrival day, a calendar day of the property.</param>
    /// <param name="dueAtUtc">The deadline (<see cref="TicinoPendingDay.DueAtUtc"/>).</param>
    /// <param name="propertyZone">The clock of the property (<c>Property.Timezone</c>, <see cref="RomeCalendar.ZoneOf"/>).</param>
    /// <param name="firstExpectedArrival">The earliest expected check-in of the day (<see cref="TicinoFirstExpectedArrival"/>), or <c>null</c>.</param>
    /// <param name="options">The thresholds.</param>
    /// <param name="nowUtc">The instant of the run.</param>
    public static ComplianceAlertStep? DueTicinoStep(
        Guid propertyId,
        DateOnly arrivalDay,
        DateTime dueAtUtc,
        TimeZoneInfo propertyZone,
        TimeOnly? firstExpectedArrival,
        ComplianceOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(propertyZone);
        ArgumentNullException.ThrowIfNull(options);

        var due = AsUtc(dueAtUtc);
        var now = AsUtc(nowUtc);
        var reminders = Math.Clamp(options.TicinoMaxOverdueReminders, 0, 30);
        var every = TimeSpan.FromHours(Math.Clamp(options.TicinoOverdueReminderHours, 1, 24 * 30));

        ComplianceAlertStep Step(int stage, ComplianceAlertKind kind, int reminder = 0) => new(
            ComplianceAlertType.TicinoNotification,
            TicinoReferenceKey(arrivalDay),
            stage,
            new ComplianceAlert(
                ComplianceAlertType.TicinoNotification,
                propertyId,
                kind,
                ReminderNumber: reminder,
                MaxReminders: reminders,
                ArrivalDay: arrivalDay,
                DueAtUtc: due));

        for (var reminder = reminders; reminder >= 1; reminder--)
        {
            var reminderAt = due + (every * reminder);
            if (now >= reminderAt)
            {
                return reminder == reminders && now >= reminderAt + LastStageValidity
                    ? null
                    : Step(ComplianceAlertStage.TicinoOverdueReminder(reminder), ComplianceAlertKind.TicinoOverdue, reminder);
            }
        }

        if (now >= due)
        {
            return reminders == 0 && now >= due + LastStageValidity
                ? null
                : Step(ComplianceAlertStage.TicinoOverdue, ComplianceAlertKind.TicinoOverdue);
        }

        if (now >= due - TimeSpan.FromHours(Math.Clamp(options.TicinoDueWarningHours, 0, 24 * 30)))
            return Step(ComplianceAlertStage.TicinoDeadlineApproaching, ComplianceAlertKind.TicinoDeadlineApproaching);

        return now >= TicinoArrivalAlertAtUtc(arrivalDay, propertyZone, firstExpectedArrival, options)
            ? Step(ComplianceAlertStage.TicinoFileToPrepare, ComplianceAlertKind.TicinoFileToPrepare)
            : null;
    }
}
