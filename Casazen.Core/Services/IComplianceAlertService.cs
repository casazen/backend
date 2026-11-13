using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// The alerts about a property that are not tied to one stay (E03/F02/US02/T01, NF-52, CHK-12): the ISTAT and tourist tax periods and the
/// connection of the property to the channel manager. The hourly <c>stay-alerts</c> job runs it after the stay alerts
/// (<see cref="IStayAlertService"/>), with the same mechanism: each stage is sent at most once, by an atomic compare-and-set on the
/// <see cref="ComplianceAlertState"/> row of the property, whatever the number of runs, retries or concurrent runs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What decides.</b> Whether a period is open comes from the compliance status engine (<see cref="IComplianceStatusService"/>, the one
/// place where a light is decided: green only with an archived proof); the connection from <see cref="IComplianceConnectionHealthService"/>.
/// This service decides only <i>when</i> to tell (<see cref="ComplianceAlertSchedule"/>, every threshold from <see cref="ComplianceOptions"/>).</item>
/// <item><b>Silent where nothing is known.</b> A region with no ISTAT deadline day and a comune with no tourist tax deadline day produce no
/// alert: a deadline is never guessed (<see cref="ITouristTaxDeadlineSource"/>: no table holds the day of a comune yet).</item>
/// <item><b>Behind the module's flag.</b> With <c>Features:ComplianceTrafficLight</c> off the run does nothing.</item>
/// </list>
/// </remarks>
public interface IComplianceAlertService
{
    /// <summary>Sends every period and connection alert due now. Never throws for one org or property that cannot be processed: it is logged and the run goes on.</summary>
    Task<ComplianceAlertRunResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one run of <see cref="IComplianceAlertService.RunAsync"/>.</summary>
/// <param name="Skipped">The module is off (<c>Features:ComplianceTrafficLight</c>): the run did nothing.</param>
/// <param name="Sent">Alerts delivered (each one email plus one push).</param>
public sealed record ComplianceAlertRunResult(bool Skipped, int Sent);

/// <summary>What a period or connection alert tells the host.</summary>
public enum ComplianceAlertKind
{
    /// <summary>The deadline of an ISTAT or tourist tax period is approaching and nothing proves it was met.</summary>
    PeriodDeadlineApproaching,

    /// <summary>The deadline of a period passed (first alert, then the limited reminders).</summary>
    PeriodOverdue,

    /// <summary>The police send of the channel manager is not active while a stay is coming (first alert, then one reminder).</summary>
    ConnectionInactive,
}

/// <summary>One alert to deliver for a property.</summary>
/// <param name="Type">ISTAT period, tourist tax period or connection.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Kind">What to tell.</param>
/// <param name="Period">The month (<c>yyyy-MM</c>) of a period alert; <c>null</c> for the connection.</param>
/// <param name="DeadlineDay">The last day on which the period could be settled (Europe/Rome); <c>null</c> for the connection.</param>
/// <param name="ReminderNumber">1..<paramref name="MaxReminders"/> for the reminders, 0 otherwise.</param>
/// <param name="MaxReminders">Configured maximum of reminders of a period.</param>
public sealed record ComplianceAlert(
    ComplianceAlertType Type,
    Guid PropertyId,
    ComplianceAlertKind Kind,
    string? Period = null,
    DateOnly? DeadlineDay = null,
    int ReminderNumber = 0,
    int MaxReminders = 0);

/// <summary>Stage due now for a property, with the alert to send when the stage is claimed.</summary>
/// <param name="Type">The kind of state row the stage belongs to.</param>
/// <param name="ReferenceKey">The reference of the state row: the period (<c>yyyy-MM</c>) or <see cref="ComplianceAlertState.ConnectionReferenceKey"/>.</param>
/// <param name="Stage">The stage (<see cref="ComplianceAlertStage"/>).</param>
/// <param name="Alert">What to send.</param>
public sealed record ComplianceAlertStep(ComplianceAlertType Type, string ReferenceKey, int Stage, ComplianceAlert Alert);

/// <summary>Stages of <see cref="ComplianceAlertType"/>: each one is sent at most once per property and reference.</summary>
public static class ComplianceAlertStage
{
    /// <summary>Period: the deadline day is approaching.</summary>
    public const int DeadlineApproaching = 1;

    /// <summary>Period: the deadline day is over and nothing proves the period was settled.</summary>
    public const int Overdue = 2;

    /// <summary>Connection: the connection was found inactive while a stay is coming.</summary>
    public const int ConnectionDetected = 1;

    /// <summary>Connection: the second and last alert, a few days after the first.</summary>
    public const int ConnectionReminder = 2;

    /// <summary>Stage of the <paramref name="number"/>-th reminder of a period after <see cref="Overdue"/> (1-based).</summary>
    public static int OverdueReminder(int number) => Overdue + number;
}

/// <summary>
/// When each period and connection alert is due (E03/F02/US02/T01, NF-52). Pure rules on UTC instants and options: every threshold is a
/// configuration value, never a constant here. Like the stay alerts, the job sends only the highest stage due and only when it is higher than
/// the last one sent, so a missed run is caught up by the next with one message, never with the backlog, and the last stage of a sequence is
/// dropped when it is older than <see cref="LastStageValidity"/>.
/// </summary>
public static class ComplianceAlertSchedule
{
    /// <summary>How long the last stage of a sequence stays due after its time.</summary>
    public static readonly TimeSpan LastStageValidity = TimeSpan.FromHours(24);

    /// <summary>
    /// A legal deadline is «near» when it is at most this far from now, before or after it: the alert also reaches the popup router
    /// (ADR-025). One definition, for the stay alerts and the period alerts.
    /// </summary>
    public static readonly TimeSpan LegalDeadlinePopupWindow = TimeSpan.FromHours(24);

    /// <summary>True when <paramref name="deadlineUtc"/> is within <see cref="LegalDeadlinePopupWindow"/> of <paramref name="nowUtc"/>.</summary>
    public static bool IsLegalDeadlineNear(DateTime? deadlineUtc, DateTime nowUtc) =>
        deadlineUtc is { } deadline && (deadline - nowUtc).Duration() <= LegalDeadlinePopupWindow;

    /// <summary>
    /// The instant a period's deadline passes: the end of the deadline day in Europe/Rome. It is what the status engine means by «overdue»
    /// (<c>ComplianceLightRules.Istat</c> turns red the day after), so an alert never says «overdue» while the cockpit still says «open».
    /// </summary>
    public static DateTime DeadlineEndUtc(DateOnly deadlineDay) => RomeCalendar.StartOfDayUtc(deadlineDay.AddDays(1));

    /// <summary>
    /// The highest stage due now for the ISTAT or tourist tax <paramref name="period"/> of a property whose last day is <paramref name="deadlineDay"/>
    /// and that nothing proves settled, or <c>null</c> when none is due yet or the last stage is more than <see cref="LastStageValidity"/> old.
    /// Sequence: the deadline approaching <see cref="ComplianceOptions.PeriodWarningDays"/> before the deadline day; overdue the morning after it;
    /// then at most <see cref="ComplianceOptions.MaxPeriodReminders"/> reminders every <see cref="ComplianceOptions.PeriodReminderDays"/> days, all
    /// at <see cref="ComplianceOptions.PeriodAlertHourLocal"/> (Europe/Rome).
    /// </summary>
    public static ComplianceAlertStep? DuePeriodStep(
        ComplianceAlertType type,
        Guid propertyId,
        string period,
        DateOnly deadlineDay,
        ComplianceOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(period);
        if (type is not (ComplianceAlertType.IstatPeriod or ComplianceAlertType.TouristTaxPeriod))
            throw new ArgumentException("A period alert is about an ISTAT or a tourist tax period.", nameof(type));

        var reminders = Math.Max(0, options.MaxPeriodReminders);
        var every = Math.Max(1, options.PeriodReminderDays);

        ComplianceAlertStep Step(int stage, ComplianceAlertKind kind, int reminder = 0) => new(
            type,
            period,
            stage,
            new ComplianceAlert(type, propertyId, kind, period, deadlineDay, reminder, reminders));

        for (var reminder = reminders; reminder >= 1; reminder--)
        {
            var reminderAt = AlertAtUtc(deadlineDay.AddDays(1 + (reminder * every)), options.PeriodAlertHourLocal);
            if (nowUtc >= reminderAt)
            {
                return reminder == reminders && nowUtc >= reminderAt + LastStageValidity
                    ? null
                    : Step(ComplianceAlertStage.OverdueReminder(reminder), ComplianceAlertKind.PeriodOverdue, reminder);
            }
        }

        var overdueAt = AlertAtUtc(deadlineDay.AddDays(1), options.PeriodAlertHourLocal);
        if (nowUtc >= overdueAt)
        {
            return reminders == 0 && nowUtc >= overdueAt + LastStageValidity
                ? null
                : Step(ComplianceAlertStage.Overdue, ComplianceAlertKind.PeriodOverdue);
        }

        var warningAt = AlertAtUtc(deadlineDay.AddDays(-Math.Max(0, options.PeriodWarningDays)), options.PeriodAlertHourLocal);
        return nowUtc >= warningAt ? Step(ComplianceAlertStage.DeadlineApproaching, ComplianceAlertKind.PeriodDeadlineApproaching) : null;
    }

    /// <summary>
    /// The next stage of the alerts about an inactive connection of <paramref name="propertyId"/> (the caller checked that a stay arrives within
    /// <see cref="ComplianceOptions.ConnectionAlertDays"/>): stage 1 at detection (<paramref name="currentStage"/> 0), stage 2
    /// <see cref="ComplianceOptions.ConnectionReminderDays"/> after the first alert and not for longer than <see cref="LastStageValidity"/> after
    /// that, then nothing: at most two messages for one incident. <c>null</c> when nothing is due.
    /// </summary>
    public static ComplianceAlertStep? DueConnectionStep(
        Guid propertyId,
        int currentStage,
        DateTime? lastAlertAtUtc,
        ComplianceOptions options,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(options);

        ComplianceAlertStep Step(int stage, int reminder) => new(
            ComplianceAlertType.ConnectionInactive,
            ComplianceAlertState.ConnectionReferenceKey,
            stage,
            new ComplianceAlert(ComplianceAlertType.ConnectionInactive, propertyId, ComplianceAlertKind.ConnectionInactive, ReminderNumber: reminder, MaxReminders: 1));

        if (currentStage <= 0)
            return Step(ComplianceAlertStage.ConnectionDetected, 0);

        if (currentStage == ComplianceAlertStage.ConnectionDetected && lastAlertAtUtc is { } last)
        {
            var reminderAt = AsUtc(last).AddDays(Math.Max(1, options.ConnectionReminderDays));
            if (nowUtc >= reminderAt && nowUtc < reminderAt + LastStageValidity)
                return Step(ComplianceAlertStage.ConnectionReminder, 1);
        }

        return null;
    }

    /// <summary>The wall-clock <paramref name="hourLocal"/> of <paramref name="day"/> in Europe/Rome, as a UTC instant.</summary>
    public static DateTime AlertAtUtc(DateOnly day, int hourLocal) =>
        RomeCalendar.ToUtc(day, new TimeOnly(Math.Clamp(hourLocal, 0, 23), 0));

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}

/// <summary>
/// The day by which the tourist tax of a month is due for the comune of a property: the source the tourist tax alerts need and today does not
/// exist (E03/F02/US02/T01). No table holds the deadline day of a comune, and CasaZen never guesses one, so the registered implementation
/// answers <c>null</c> and the alert stays silent; the day a source of the day is decided (an open point of the plan), only this implementation changes.
/// </summary>
public interface ITouristTaxDeadlineSource
{
    /// <summary>
    /// The last day on which the tourist tax of the month starting on <paramref name="periodStart"/> can be settled for the comune of the property,
    /// or <c>null</c> when it is not known.
    /// </summary>
    Task<DateOnly?> DeadlineOfAsync(Guid propertyId, DateOnly periodStart, CancellationToken ct);
}

/// <summary>
/// Who receives a compliance alert (E03/F02/US02/T01, NF-52): the port the per-person preferences of NF-65 replace. The default implementation is
/// today's audience, narrowed to the people who may hear about the property (<c>HostScope</c>): the organization's address, and the people of
/// <see cref="INotificationPreferenceService"/> for the event «legal deadline», which is the one place that applies the reach of a collaborator
/// «Solo alcuni». Its answer never holds more than ids and addresses.
/// </summary>
public interface IComplianceAlertRecipientResolver
{
    /// <summary>
    /// Who is told about an alert of one property: the organization's address and the people who reach the property (the person in charge while
    /// they still reach it, and the administrators). A collaborator limited to some properties is among them only for a property it was given.
    /// </summary>
    Task<ComplianceAlertRecipients> ForPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct);

    /// <summary>
    /// Who is told about something of the organization as a whole, with no property (the monthly summary): the organization's address and the
    /// addresses of its administrators. Nobody limited to some properties is among them: the thing is outside every «Solo alcuni» reach.
    /// </summary>
    Task<ComplianceAlertRecipients> ForOrganizationAsync(Guid orgId, CancellationToken ct);
}

/// <summary>The recipients of a compliance alert.</summary>
/// <param name="Emails">The addresses to email, distinct.</param>
/// <param name="UserIds">The people (<c>User.Id</c>) to alert in the app (the popup alerts), distinct.</param>
public sealed record ComplianceAlertRecipients(IReadOnlyList<string> Emails, IReadOnlyList<string> UserIds)
{
    /// <summary>Nobody.</summary>
    public static ComplianceAlertRecipients None { get; } = new([], []);
}

/// <summary>A popup alert to raise for the people who may hear about a property (<see cref="IComplianceAlertFollowUp.PopupAsync"/>).</summary>
/// <param name="OrgId">The org of the property.</param>
/// <param name="PropertyId">The property the audience is the one of.</param>
/// <param name="Type">What the alert is about (<see cref="AlertType.AlloggiatiDeadline"/> and <see cref="AlertType.IstatDeadline"/> are the legal ones).</param>
/// <param name="EntityType">The kind of the entity the client opens (<see cref="PopupAlert.BookingEntityType"/>, <see cref="PopupAlert.PropertyEntityType"/>).</param>
/// <param name="EntityId">The id of that entity: the id of the cockpit item.</param>
/// <param name="DeadlineUtc">The legal deadline the alert is about (UTC).</param>
/// <param name="UserIds">
/// The people to alert (<c>User.Id</c>) when the caller already read the audience of the property (the alerts about a period or the connection read it
/// before the stage is claimed, for the email as well): they are used as they are, with no second read. <c>null</c> = not read yet, the follow-up asks the
/// recipient port; an empty list is nobody, never «everybody».
/// </param>
public sealed record ComplianceAlertPopup(
    Guid OrgId,
    Guid PropertyId,
    AlertType Type,
    string EntityType,
    Guid EntityId,
    DateTime? DeadlineUtc,
    IReadOnlyList<string>? UserIds = null);

/// <summary>
/// What a stage that is sent leaves behind besides its email and its push (E03/F02/US02/T01, NF-48, ADR-025, ADR-026): the "To do" item of the
/// condition and, for a legal deadline within 24 hours, a popup alert. Every method is idempotent, so a retry never gives anybody a second one.
/// </summary>
public interface IComplianceAlertFollowUp
{
    /// <summary>
    /// Opens the "To do" of an alert stage, or keeps it open (<see cref="IInternalActivityService.CreateAsync"/>: idempotent per type, entity type
    /// and entity id while open). A property that is gone (deleted since) has nothing to do and is not an error.
    /// </summary>
    Task OpenTodoAsync(CreateTodoRequest request, CancellationToken ct);

    /// <summary>
    /// Alerts, through the popup router (<see cref="IAlertRouter"/>, which applies the quiet hours of each person and never holds back a legal
    /// alert), the people who reach the property of <paramref name="popup"/>, when its deadline is within
    /// <see cref="ComplianceAlertSchedule.LegalDeadlinePopupWindow"/> of <paramref name="nowUtc"/>; otherwise nothing. The people are
    /// <see cref="ComplianceAlertPopup.UserIds"/> when the caller already read the audience (no second read), else the ones the recipient port says.
    /// </summary>
    Task PopupAsync(ComplianceAlertPopup popup, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Closes, as the system, the open "To do" of these <paramref name="types"/> about these entities of <paramref name="entityType"/>: the condition
    /// they stand for is cleared (the proof arrived, the data are complete, the connection is active again).
    /// </summary>
    Task CloseTodosAsync(IReadOnlyCollection<ActivityType> types, string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct);

    /// <summary>
    /// Closes, as the system, the open "To do" of these <paramref name="types"/> about a stay that owes nothing any more (cancelled, a request, a
    /// hold or a no-show: <c>AlloggiatiStatusRules.ActiveBookingStatuses</c> and no no-show mark, the one definition). At most 500 a call, the
    /// oldest first, so a backlog is closed over several runs.
    /// </summary>
    Task CloseTodosOfStaysThatOweNothingAsync(IReadOnlyCollection<ActivityType> types, CancellationToken ct);
}
