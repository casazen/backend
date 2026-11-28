using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// The alerts about a property that are not tied to one stay (E03/F02/US02/T01, NF-52, CHK-12): the ISTAT and tourist tax periods and the
/// connection of the property to the channel manager; and, since E30/F02/US01/T01 (NF-127, D9), the 24 hours of the notification of the guests of a
/// Ticino property to the cantonal police, one sequence for each arrival day. The hourly <c>stay-alerts</c> job runs it after the stay alerts
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
/// <item><b>Behind the module's flag.</b> With <c>Features:ComplianceTrafficLight</c> off the period and connection alerts are not run. The Ticino
/// ones (<see cref="ComplianceAlertType.TicinoNotification"/>) have a flag of their own, <c>Features:TicinoNotificationAlerts</c>: with both off the run
/// does nothing.</item>
/// <item><b>Ticino.</b> The days to notify come only from <see cref="ITicinoNotificationService.ListPendingDaysAsync"/>, which is also the one place
/// that says a day is closed (a receipt, and when it exists the declaration): the alerts never read that rule again, so a closed day stops them.</item>
/// </list>
/// </remarks>
public interface IComplianceAlertService
{
    Task<ComplianceAlertRunResult> RunAsync(CancellationToken cancellationToken = default);
}

public sealed record ComplianceAlertRunResult(bool Skipped, int Sent);

public enum ComplianceAlertKind
{
    /// <summary>The deadline of an ISTAT or tourist tax period is approaching and nothing proves it was met.</summary>
    PeriodDeadlineApproaching,

    PeriodOverdue,

    ConnectionInactive,

    TicinoFileToPrepare,

    TicinoDeadlineApproaching,

    TicinoOverdue,

    SwissNotificationToPrepare,

    SwissNotificationApproaching,

    SwissNotificationOverdue,
}

/// <summary>One alert to deliver for a property.</summary>
/// <param name="Type">ISTAT period, tourist tax period or connection.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Kind">What to tell.</param>
/// <param name="Period">The month (<c>yyyy-MM</c>) of a period alert; <c>null</c> for the connection.</param>
/// <param name="DeadlineDay">The last day on which the period could be settled (Europe/Rome); <c>null</c> for the connection.</param>
/// <param name="ReminderNumber">1..<paramref name="MaxReminders"/> for the reminders, 0 otherwise.</param>
/// <param name="MaxReminders">Configured maximum of reminders of a period.</param>
/// <param name="ArrivalDay">The arrival day (property calendar) of a Ticino notification alert; <c>null</c> for the others.</param>
/// <param name="DueAtUtc">The instant the notification of that arrival day is due (24 hours from the first recorded arrival, else the end of the day); <c>null</c> for the others.</param>
/// <param name="MissingFields">
/// What the data of that day still miss, as stable codes (<c>TicinoMissing</c>, <see cref="TicinoAlertMissing"/>): types of field, never a name or a value.
/// <c>null</c> or empty when nothing is known to miss.
/// </param>
public sealed record ComplianceAlert(
    ComplianceAlertType Type,
    Guid PropertyId,
    ComplianceAlertKind Kind,
    string? Period = null,
    DateOnly? DeadlineDay = null,
    int ReminderNumber = 0,
    int MaxReminders = 0,
    DateOnly? ArrivalDay = null,
    DateTime? DueAtUtc = null,
    IReadOnlyList<string>? MissingFields = null);

public sealed record ComplianceAlertStep(ComplianceAlertType Type, string ReferenceKey, int Stage, ComplianceAlert Alert);

public static class ComplianceAlertStage
{
    public const int DeadlineApproaching = 1;

    public const int Overdue = 2;

    public const int ConnectionDetected = 1;

    public const int ConnectionReminder = 2;

    public static int OverdueReminder(int number) => Overdue + number;

    public const int TicinoFileToPrepare = 1;

    public const int TicinoDeadlineApproaching = 2;

    public const int TicinoOverdue = 3;

    public static int TicinoOverdueReminder(int number) => TicinoOverdue + number;
}

public static class TicinoAlertMissing
{
    public const string StructureId = "structureId";
}

/// <summary>
/// When each period and connection alert is due (E03/F02/US02/T01, NF-52). Pure rules on UTC instants and options: every threshold is a
/// configuration value, never a constant here. Like the stay alerts, the job sends only the highest stage due and only when it is higher than
/// the last one sent, so a missed run is caught up by the next with one message, never with the backlog, and the last stage of a sequence is
/// dropped when it is older than <see cref="LastStageValidity"/>.
/// </summary>
public static partial class ComplianceAlertSchedule
{
    public static readonly TimeSpan LastStageValidity = TimeSpan.FromHours(24);

    public static readonly TimeSpan LegalDeadlinePopupWindow = TimeSpan.FromHours(24);

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
    Task<DateOnly?> DeadlineOfAsync(Guid propertyId, DateOnly periodStart, CancellationToken ct);
}

/// <summary>
/// Who may hear about an alert of a given <see cref="ComplianceAlertType"/> beyond the people who reach the property (E30/F02/US01/T01, E31/F02/US01/T01, NF-127, NF-131): the one definition of
/// the alerts that go only to the people who <b>can act on them</b>. The notification of the guests to the police of Ticino, and to the authority of another canton, is declared and its
/// receipt recorded with <c>alloggiati.submit</c> (<c>CasazenPolicies.AlloggiatiSubmit</c>), so a person who reaches the property but does not hold it (a collaborator, an accountant) is not
/// told about it in the app: no push, no entry of the bell, no live update. The alerts of the Italian Alloggiati and ISTAT deadlines, the ones about a period and the connection and the monthly
/// summary keep the audience they always had (<see cref="RequiredPermissionOf"/> is <c>null</c> for them).
/// </summary>
/// <remarks>
/// <para>The permission is the one the policy asks, in the context the policy names, and it is judged by the one evaluation of a permission: <see cref="IContextAuthorizationService"/>, the one the
/// policy handler uses (roles of the memberships, the owner on the token before the team, the host onboarding, a deactivated member never). Nothing here rebuilds who holds a permission or who
/// reaches a property: <see cref="INotificationPreferenceService"/> keeps deciding the reach, and the permission only narrows it.</para>
/// <para>The <b>e-mail</b> is not narrowed: it goes to the contact address of the organization, which is the address of the organization and not a person.</para>
/// </remarks>
public static class ComplianceAlertAudience
{
    /// <summary>
    /// <c>alloggiati.submit</c> in the short-rent context: the permission that declares a day notified, records the receipt of the portal and registers the guests of a stay. It is exactly the
    /// permission of <c>CasazenPolicies.AlloggiatiSubmit</c> (<c>RequireContext:short-rent:alloggiati.submit</c>); a test compares the two.
    /// </summary>
    public static NotificationPermission AlloggiatiSubmit { get; } = new(OrgRoleCatalog.ShortRent, HostPermissions.AlloggiatiSubmit);

    /// <summary>
    /// The permission the people told about an alert of <paramref name="type"/> must hold on top of reaching the property: <see cref="AlloggiatiSubmit"/> for the notification of the guests of
    /// Ticino (<see cref="ComplianceAlertType.TicinoNotification"/>) and of the other cantons (<see cref="ComplianceAlertType.SwissNotification"/>); <c>null</c> for every other alert,
    /// whose audience is unchanged.
    /// </summary>
    public static NotificationPermission? RequiredPermissionOf(ComplianceAlertType type) =>
        type is ComplianceAlertType.TicinoNotification or ComplianceAlertType.SwissNotification ? AlloggiatiSubmit : null;
}

/// <summary>
/// Who receives a compliance alert (E03/F02/US02/T01, NF-52): the port the per-person preferences of NF-65 replace. The default implementation is
/// today's audience, narrowed to the people who may hear about the property (<c>HostScope</c>): the organization's address, and the people of
/// <see cref="INotificationPreferenceService"/> for the event «legal deadline», which is the one place that applies the reach of a collaborator
/// «Solo alcuni». Its answer never holds more than ids and addresses.
/// </summary>
public interface IComplianceAlertRecipientResolver
{
    Task<ComplianceAlertRecipients> ForPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct);

    /// <summary>
    /// As <see cref="ForPropertyAsync(Guid, Guid, CancellationToken)"/>, with the people narrowed to the ones who also hold <paramref name="requiredPermission"/> (an alert they can act on:
    /// <see cref="ComplianceAlertAudience.RequiredPermissionOf"/>), on top of every reach rule (the person in charge while they still reach the property, the administrators, a collaborator
    /// «Solo alcuni» only for a property it was given, a deactivated member never). The reach stays <see cref="INotificationPreferenceService"/>'s: the permission is asked of it
    /// (<see cref="NotificationEventContext.RequiredPermission"/>) and never rebuilt here. <see cref="ComplianceAlertRecipients.Emails"/> is the address of the organization as before: it is the
    /// organization's, not a person. <see cref="ComplianceAlertRecipients.UserIds"/> may be empty (nobody who reaches the property holds the permission): nobody is told in the app and the
    /// alert goes on as it does today.
    /// </summary>
    Task<ComplianceAlertRecipients> ForPropertyAsync(Guid orgId, Guid propertyId, NotificationPermission requiredPermission, CancellationToken ct);

    Task<ComplianceAlertRecipients> ForOrganizationAsync(Guid orgId, CancellationToken ct);
}

public sealed record ComplianceAlertRecipients(IReadOnlyList<string> Emails, IReadOnlyList<string> UserIds)
{
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

    Task CloseTodosAsync(IReadOnlyCollection<ActivityType> types, string entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct);

    Task CloseTodosOfStaysThatOweNothingAsync(IReadOnlyCollection<ActivityType> types, CancellationToken ct);
}
