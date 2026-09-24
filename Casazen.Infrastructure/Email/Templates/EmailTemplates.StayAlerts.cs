using System.Globalization;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>Short plain-text notification (push): no markup, values not encoded.</summary>
public sealed record PushText(string Title, string Body);

/// <summary>
/// Host alerts about a stay (CO-10): the stages of the Alloggiati Web sequence and the check-out reminder, as email and
/// as push text. The first two stages use <see cref="GuestCheckInIncomplete"/> and <see cref="AlloggiatiDeadline"/>.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string AlloggiatiOverdue = "alloggiati-overdue";
        public const string AlloggiatiFailed = "alloggiati-failed";
        public const string CheckoutReminder = "checkout-reminder";
    }

    /// <summary>
    /// Alloggiati Web deadline passed without the communication, to the host: the first alert
    /// (<paramref name="reminderNumber"/> 0) and the daily reminders ("reminder n of max"). The exact deadline is shown
    /// when the arrival is registered (<paramref name="deadlineUtc"/>); otherwise the text says the day of arrival is
    /// over.
    /// </summary>
    public static EmailContent AlloggiatiOverdue(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime? deadlineUtc,
        int reminderNumber = 0,
        int maxReminders = 0)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("AlloggiatiOverdue_Body", guestName, propertyName, checkInDate);
        builder = deadlineUtc is { } deadline
            ? builder.Paragraph("AlloggiatiDeadline_DeadlineAt", builder.FormatInstant(deadline))
            : builder.Muted("AlloggiatiOverdue_ArrivalNotRegistered");
        builder = builder.Paragraph("AlloggiatiOverdue_Action");
        if (reminderNumber > 0)
            builder = builder.Muted("AlloggiatiOverdue_Reminder", reminderNumber, Math.Max(reminderNumber, maxReminders));
        return builder.Build("AlloggiatiOverdue_Subject", propertyName, checkInDate);
    }

    /// <summary>Alloggiati Web communication rejected by the portal or failed, to the host. No technical code is shown.</summary>
    public static EmailContent AlloggiatiFailed(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("AlloggiatiFailed_Body", guestName, propertyName, checkInDate)
            .Paragraph("AlloggiatiFailed_Action")
            .Build("AlloggiatiFailed_Subject", propertyName, checkInDate);

    /// <summary>Check-out day of a confirmed or checked-in stay, to the host (A5-25: email as well as push).</summary>
    public static EmailContent CheckoutReminder(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkOutDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("CheckoutReminder_Body", guestName, propertyName, checkOutDate)
            .Paragraph("CheckoutReminder_Action")
            .Build("CheckoutReminder_Subject", propertyName, checkOutDate);

    /// <summary>
    /// Push text of an Alloggiati stay alert: property name and check-in date. The check-out reminder keeps the push of
    /// <see cref="IPushNotificationService.SendCheckoutReminderAsync"/>.
    /// </summary>
    public static PushText StayAlertPush(CultureInfo culture, StayAlertKind kind, string propertyName, DateTime checkInDate)
    {
        var prefix = kind switch
        {
            StayAlertKind.GuestDataMissing => "StayAlertPush_GuestDataMissing",
            StayAlertKind.AlloggiatiDeadlineApproaching => "StayAlertPush_DeadlineApproaching",
            StayAlertKind.AlloggiatiOverdue => "StayAlertPush_Overdue",
            StayAlertKind.AlloggiatiFailed => "StayAlertPush_Failed",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No push text for this alert."),
        };
        var date = checkInDate.ToString(EmailTexts.Get("Format_Date", culture), culture);
        return new PushText(
            EmailTexts.Get($"{prefix}_Title", culture),
            string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), propertyName, date));
    }
}
