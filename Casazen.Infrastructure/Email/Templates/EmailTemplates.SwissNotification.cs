using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The alerts about the notification of the guests of a property in a canton other than Ticino to the cantonal authority (E31/F02/US01/T01, NF-131), as e-mail and as push text: the
/// property, the arrival day, the deadline of the canton and what the data of the day miss as <b>types of field</b> (the codes of <c>TicinoMissing</c>, translated by the same texts the
/// Ticino alerts use), never the name of a guest, a value, a document or a booking code. The authority is named in general («autorità cantonale»): the canton is the one of the
/// property, and no supplier of software or of a cantonal system is named. Texts in Italian and English (<c>EmailTexts.resx</c>, <c>EmailTexts.en.resx</c>): like every e-mail to a host
/// they are not translated into the guest languages (<c>EmailTextsParityTests</c>).
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string SwissNotificationAlert = "swiss-notification-alert";
    }

    /// <summary>
    /// An arrival day of a property in a canton other than Ticino still to notify, to the host (<see cref="ComplianceAlertKind.SwissNotificationToPrepare"/>: the day of the arrival;
    /// <see cref="ComplianceAlertKind.SwissNotificationApproaching"/>: the deadline is near; <see cref="ComplianceAlertKind.SwissNotificationOverdue"/>: it is over, with the number of the
    /// reminder). The button leads to the compliance page of the console when its link is known.
    /// </summary>
    public static EmailContent SwissNotificationAlert(CultureInfo culture, ComplianceAlert alert, string propertyName, string? complianceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var phase = SwissPhase(alert);
        var builder = new EmailHtmlBuilder(culture);
        var day = alert.ArrivalDay?.ToDateTime(TimeOnly.MinValue);
        var deadline = alert.DueAtUtc is { } due ? builder.FormatInstant(due) : string.Empty;

        builder = builder.Paragraph($"SwissNotificationAlert_{phase}_Body", propertyName, day, deadline);
        if (TicinoMissingLabels(alert.MissingFields, culture) is { Length: > 0 } missing)
            builder = builder.Paragraph("SwissNotificationAlert_Missing", missing);

        builder = builder.Paragraph("SwissNotificationAlert_Action");
        if (alert.ReminderNumber > 0)
            builder = builder.Muted("AlloggiatiOverdue_Reminder", alert.ReminderNumber, Math.Max(alert.ReminderNumber, alert.MaxReminders));

        return WithComplianceLink(builder, complianceUrl).Build($"SwissNotificationAlert_{phase}_Subject", propertyName, day);
    }

    /// <summary>
    /// Push text of an arrival day of a canton other than Ticino (E31/F02/US01/T01): the property and the day, the deadline while it is ahead, and the types of field that are missing.
    /// Plain text on a lock screen, no guest data.
    /// </summary>
    public static PushText SwissNotificationPush(CultureInfo culture, ComplianceAlert alert, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var overdue = alert.Kind == ComplianceAlertKind.SwissNotificationOverdue;
        var prefix = overdue ? "SwissNotificationPush_Overdue" : "SwissNotificationPush_Due";
        var day = alert.ArrivalDay?.ToDateTime(TimeOnly.MinValue).ToString(EmailTexts.Get("Format_Date", culture), culture) ?? string.Empty;
        var deadline = alert.DueAtUtc is { } due
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(due, DateTimeKind.Utc), RomeCalendar.TimeZone).ToString(EmailTexts.Get("Format_DateTime", culture), culture)
            : string.Empty;

        var body = string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), propertyName, day, deadline);
        if (TicinoMissingLabels(alert.MissingFields, culture) is { Length: > 0 } missing)
            body += " " + string.Format(culture, EmailTexts.Get("SwissNotificationPush_Missing", culture), missing);

        return new PushText(EmailTexts.Get($"{prefix}_Title", culture), body);
    }

    /// <summary>
    /// Push text of the declaration of an arrival day of a canton other than Ticino as notified to the authority, or of its undo (E31/F02/US01/T01, NF-131): the property and the arrival day.
    /// Plain text on a lock screen: never a guest, a value, a document or the person who did it (the cockpit shows who).
    /// </summary>
    public static PushText SwissNotificationDeclaredPush(CultureInfo culture, string propertyName, DateOnly day, bool declared)
    {
        var date = day.ToDateTime(TimeOnly.MinValue).ToString(EmailTexts.Get("Format_Date", culture), culture);
        return Push(culture, declared ? "Push_SwissNotificationDeclared" : "Push_SwissNotificationUndone", propertyName, date);
    }

    private static string SwissPhase(ComplianceAlert alert) => alert.Kind switch
    {
        ComplianceAlertKind.SwissNotificationToPrepare => "ToPrepare",
        ComplianceAlertKind.SwissNotificationApproaching => "Approaching",
        ComplianceAlertKind.SwissNotificationOverdue => "Overdue",
        _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.Kind, "Not an alert about the notification of an arrival day of a canton other than Ticino."),
    };
}
