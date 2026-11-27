using Casazen.Core.Entities;
using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>
/// When the alerts about the notification of the guests of a property in a canton other than Ticino are due (E31/F02/US01/T01, NF-131): the same three stages as the 24 hours of
/// Ticino, measured against the deadline of the regime of the canton instead of the 24 hours. Pure rules on UTC instants, the zone of the property and
/// <see cref="ComplianceOptions"/>.
/// </summary>
/// <remarks>
/// <para><b>One sequence for each arrival day of a property</b>, as long as the day is still to notify (the days, and the instant each is due, come from
/// <see cref="ISwissNotificationService.ListPendingDaysAsync"/>, which is also the one place that says a day is closed). A day whose regime has no deadline has no alert: a deadline is
/// never guessed.</para>
/// <para><b>The stages are the ones of the Ticino sequence</b> (<see cref="DueTicinoStep"/>, which holds the rules and the tests of the clock, the daylight saving changes and the
/// outages): the arrival day (<see cref="ComplianceOptions.TicinoArrivalAlertDelayHours"/> after the first expected check-in, else <see cref="ComplianceOptions.TicinoArrivalAlertDefaultHourLocal"/>,
/// never after 23:00), <see cref="ComplianceOptions.TicinoDueWarningHours"/> before the deadline, the deadline, then at most <see cref="ComplianceOptions.TicinoMaxOverdueReminders"/>
/// reminders <see cref="ComplianceOptions.TicinoOverdueReminderHours"/> apart. The thresholds are the same options: no new configuration, so a host that sets them sets them for both. The stage
/// numbers are the Ticino ones (<see cref="ComplianceAlertStage.TicinoFileToPrepare"/> = 1 to <see cref="ComplianceAlertStage.TicinoOverdueReminder"/>); the row of the state is another one, because its
/// type is <see cref="ComplianceAlertType.SwissNotification"/>. Where the deadline is the arrival itself (<c>AtArrival</c>) a day whose arrival is recorded is overdue from that moment on, so
/// its first alert is the overdue one, at the first hourly run after it.</para>
/// </remarks>
public static partial class ComplianceAlertSchedule
{
    /// <summary>
    /// The <see cref="ComplianceAlertState.ReferenceKey"/> of the alerts of an arrival day: <c>yyyy-MM-dd</c>, the key of the Ticino alerts (the type of the row tells the two apart; the
    /// property is the other column of the unique index).
    /// </summary>
    public static string SwissReferenceKey(DateOnly arrivalDay) => TicinoReferenceKey(arrivalDay);

    /// <summary>
    /// The highest stage due now for the notification of the arrival day <paramref name="arrivalDay"/> of a property in a canton other than Ticino whose deadline is
    /// <paramref name="dueAtUtc"/> and that nothing closed, or <c>null</c> when none is due yet or the last stage is more than <see cref="LastStageValidity"/> old. The sequence is the one of
    /// the remarks of this class; the alert is of type <see cref="ComplianceAlertType.SwissNotification"/> with the kinds
    /// <see cref="ComplianceAlertKind.SwissNotificationToPrepare"/>, <see cref="ComplianceAlertKind.SwissNotificationApproaching"/> and <see cref="ComplianceAlertKind.SwissNotificationOverdue"/>.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="arrivalDay">The arrival day, a calendar day of the property.</param>
    /// <param name="dueAtUtc">The deadline (<see cref="SwissPendingDay.DueAtUtc"/>, never <c>null</c> here: a regime with no deadline has no alert).</param>
    /// <param name="propertyZone">The clock of the property (<c>Property.Timezone</c>).</param>
    /// <param name="firstExpectedArrival">The earliest expected check-in of the day (<see cref="TicinoFirstExpectedArrival"/>), or <c>null</c>.</param>
    /// <param name="options">The thresholds.</param>
    /// <param name="nowUtc">The instant of the run.</param>
    public static ComplianceAlertStep? DueSwissStep(
        Guid propertyId,
        DateOnly arrivalDay,
        DateTime dueAtUtc,
        TimeZoneInfo propertyZone,
        TimeOnly? firstExpectedArrival,
        ComplianceOptions options,
        DateTime nowUtc)
    {
        var step = DueTicinoStep(propertyId, arrivalDay, dueAtUtc, propertyZone, firstExpectedArrival, options, nowUtc);
        if (step is null)
            return null;

        return new ComplianceAlertStep(
            ComplianceAlertType.SwissNotification,
            SwissReferenceKey(arrivalDay),
            step.Stage,
            step.Alert with { Type = ComplianceAlertType.SwissNotification, Kind = SwissKindOf(step.Alert.Kind) });
    }

    private static ComplianceAlertKind SwissKindOf(ComplianceAlertKind ticinoKind) => ticinoKind switch
    {
        ComplianceAlertKind.TicinoFileToPrepare => ComplianceAlertKind.SwissNotificationToPrepare,
        ComplianceAlertKind.TicinoDeadlineApproaching => ComplianceAlertKind.SwissNotificationApproaching,
        ComplianceAlertKind.TicinoOverdue => ComplianceAlertKind.SwissNotificationOverdue,
        _ => throw new ArgumentOutOfRangeException(nameof(ticinoKind), ticinoKind, "Not a stage of the notification of an arrival day."),
    };
}
