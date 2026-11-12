using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>Short plain-text notification (push): no markup, values not encoded.</summary>
public sealed record PushText(string Title, string Body);

/// <summary>
/// Host alerts about a stay (CO-10): the stages of the Alloggiati Web sequence and the check-out reminder, as email and
/// as push text. The first two stages use <see cref="GuestCheckInIncomplete"/> and <see cref="AlloggiatiDeadline"/>;
/// every alert links the booking in the console with the button of the new booking email (BK-10).
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string AlloggiatiOverdue = "alloggiati-overdue";
        public const string AlloggiatiFailed = "alloggiati-failed";
        public const string CheckoutReminder = "checkout-reminder";
        public const string ReceiptMissing = "receipt-missing";
        public const string ShortStayAtRisk = "short-stay-at-risk";
        public const string OverThirtyNights = "over-thirty-nights";
        public const string CompliancePeriodAlert = "compliance-period-alert";
        public const string ComplianceConnectionAlert = "compliance-connection-alert";
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
        int maxReminders = 0,
        string? bookingUrl = null)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("AlloggiatiOverdue_Body", guestName, propertyName, checkInDate);
        builder = deadlineUtc is { } deadline
            ? builder.Paragraph("AlloggiatiDeadline_DeadlineAt", builder.FormatInstant(deadline))
            : builder.Muted("AlloggiatiOverdue_ArrivalNotRegistered");
        builder = WithBookingLink(builder.Paragraph("AlloggiatiOverdue_Action"), bookingUrl);
        if (reminderNumber > 0)
            builder = builder.Muted("AlloggiatiOverdue_Reminder", reminderNumber, Math.Max(reminderNumber, maxReminders));
        return builder.Build("AlloggiatiOverdue_Subject", propertyName, checkInDate);
    }

    /// <summary>Alloggiati Web communication rejected by the portal or failed, to the host. No technical code is shown.</summary>
    public static EmailContent AlloggiatiFailed(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string? bookingUrl = null) =>
        WithBookingLink(
                new EmailHtmlBuilder(culture)
                    .Paragraph("AlloggiatiFailed_Body", guestName, propertyName, checkInDate)
                    .Paragraph("AlloggiatiFailed_Action"),
                bookingUrl)
            .Build("AlloggiatiFailed_Subject", propertyName, checkInDate);

    /// <summary>Check-out day of a confirmed or checked-in stay, to the host (A5-25: email as well as push).</summary>
    public static EmailContent CheckoutReminder(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkOutDate,
        string? bookingUrl = null) =>
        WithBookingLink(
                new EmailHtmlBuilder(culture)
                    .Paragraph("CheckoutReminder_Body", guestName, propertyName, checkOutDate)
                    .Paragraph("CheckoutReminder_Action"),
                bookingUrl)
            .Build("CheckoutReminder_Subject", propertyName, checkOutDate);

    /// <summary>
    /// The Alloggiati Web communication was sent, entrusted to the channel manager or declared sent, and no receipt is archived the day after
    /// (E03/F02/US02/T01, CHK-12), to the host. The text names the property and the arrival day, never the guest.
    /// </summary>
    public static EmailContent ReceiptMissing(CultureInfo culture, string propertyName, DateTime checkInDate, string? bookingUrl = null) =>
        WithBookingLink(
                new EmailHtmlBuilder(culture)
                    .Paragraph("ReceiptMissing_Body", propertyName, checkInDate)
                    .Paragraph("ReceiptMissing_Action"),
                bookingUrl)
            .Build("ReceiptMissing_Subject", propertyName, checkInDate);

    /// <summary>
    /// A stay of at most one night (the 6-hour term) that is not entrusted to the channel manager arrives within a few hours (E03/F02/US02/T01,
    /// CHK-12), to the host. The exact deadline is shown when the arrival is registered (<paramref name="deadlineUtc"/>). No guest data.
    /// </summary>
    public static EmailContent ShortStayAtRisk(
        CultureInfo culture,
        string propertyName,
        DateTime checkInDate,
        DateTime? deadlineUtc = null,
        string? bookingUrl = null)
    {
        var builder = new EmailHtmlBuilder(culture).Paragraph("ShortStayAtRisk_Body", propertyName, checkInDate);
        if (deadlineUtc is { } deadline)
            builder = builder.Paragraph("AlloggiatiDeadline_DeadlineAt", builder.FormatInstant(deadline));

        return WithBookingLink(builder.Paragraph("ShortStayAtRisk_Action"), bookingUrl)
            .Build("ShortStayAtRisk_Subject", propertyName, checkInDate);
    }

    /// <summary>
    /// A stay over 30 nights, which the channel manager does not send by itself, arrives tomorrow (E03/F02/US02/T01, CHK-12), to the host. No guest
    /// data.
    /// </summary>
    public static EmailContent OverThirtyNights(CultureInfo culture, string propertyName, DateTime checkInDate, string? bookingUrl = null) =>
        WithBookingLink(
                new EmailHtmlBuilder(culture)
                    .Paragraph("OverThirtyNights_Body", propertyName, checkInDate)
                    .Paragraph("OverThirtyNights_Action"),
                bookingUrl)
            .Build("OverThirtyNights_Subject", propertyName, checkInDate);

    /// <summary>
    /// The deadline of the ISTAT or tourist tax <paramref name="alert"/> period is approaching, or passed, with nothing to prove it was met
    /// (E03/F02/US02/T01, CHK-12), to the host: the property, the month and the deadline day, never a name. The first alert and the reminders
    /// share the text; a reminder shows its number. The button leads to the compliance page of the console when its link is known.
    /// </summary>
    public static EmailContent CompliancePeriodAlert(CultureInfo culture, ComplianceAlert alert, string propertyName, string? complianceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var prefix = alert.Type == ComplianceAlertType.TouristTaxPeriod ? "ComplianceAlertTouristTax" : "ComplianceAlertIstat";
        var phase = alert.Kind == ComplianceAlertKind.PeriodOverdue ? "Overdue" : "Approaching";
        var month = MonthLabel(alert.Period, culture);
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph($"{prefix}_{phase}_Body", propertyName, month, alert.DeadlineDay?.ToDateTime(TimeOnly.MinValue))
            .Paragraph($"{prefix}_Action");
        if (alert.ReminderNumber > 0)
            builder = builder.Muted("AlloggiatiOverdue_Reminder", alert.ReminderNumber, Math.Max(alert.ReminderNumber, alert.MaxReminders));

        return WithComplianceLink(builder, complianceUrl).Build($"{prefix}_{phase}_Subject", propertyName, month);
    }

    /// <summary>
    /// The police send of the channel manager is not active while a stay is coming (E03/F02/US02/T01, CHK-12), to the host: the property only. The
    /// second and last notice says so.
    /// </summary>
    public static EmailContent ComplianceConnectionAlert(CultureInfo culture, ComplianceAlert alert, string propertyName, string? complianceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var builder = new EmailHtmlBuilder(culture).Paragraph("ComplianceAlertConnection_Body", propertyName);
        if (alert.ReminderNumber > 0)
            builder = builder.Muted("ComplianceAlertConnection_Reminder");

        return WithComplianceLink(builder.Paragraph("ComplianceAlertConnection_Action"), complianceUrl)
            .Build("ComplianceAlertConnection_Subject", propertyName);
    }

    /// <summary>
    /// Push text of a period or connection alert: the property and, for a period, the month and the deadline day. Plain text, no guest data.
    /// </summary>
    public static PushText CompliancePush(CultureInfo culture, ComplianceAlert alert, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var phase = alert.Kind == ComplianceAlertKind.PeriodOverdue ? "Overdue" : "Approaching";
        var prefix = alert.Type switch
        {
            ComplianceAlertType.IstatPeriod => $"CompliancePush_Istat_{phase}",
            ComplianceAlertType.TouristTaxPeriod => $"CompliancePush_TouristTax_{phase}",
            ComplianceAlertType.ConnectionInactive => "CompliancePush_Connection",
            _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.Type, "No push text for this alert."),
        };
        var deadline = alert.DeadlineDay is { } day
            ? day.ToDateTime(TimeOnly.MinValue).ToString(EmailTexts.Get("Format_Date", culture), culture)
            : string.Empty;
        return new PushText(
            EmailTexts.Get($"{prefix}_Title", culture),
            string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), propertyName, MonthLabel(alert.Period, culture), deadline));
    }

    /// <summary>
    /// The month <c>yyyy-MM</c> as the texts write it, with the month name of the culture (<c>ottobre 2026</c>, <c>October 2026</c>); the value itself
    /// when it is not a month. The format is a date-only one, which <see cref="DateOnly"/> accepts.
    /// </summary>
    internal static string MonthLabel(string? period, CultureInfo culture) =>
        period is { Length: 7 }
        && DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            ? first.ToString("MMMM yyyy", culture)
            : period ?? string.Empty;

    /// <summary>
    /// "Open the compliance page" button to the compliance page of the console (<see cref="PublicSiteLinks.HostCompliance"/>), with the raw link
    /// below it. Nothing when the link is not known.
    /// </summary>
    private static EmailHtmlBuilder WithComplianceLink(EmailHtmlBuilder builder, string? complianceUrl) =>
        string.IsNullOrWhiteSpace(complianceUrl)
            ? builder
            : builder.Button("ComplianceAlert_Cta", complianceUrl).LinkFallback("Booking_LinkFallback", complianceUrl);

    /// <summary>
    /// "Open the booking" button to the booking in the console (<see cref="PublicSiteLinks.HostBooking"/>), with the raw
    /// link below it, as in the new booking email of BK-10. Nothing when the link is not known.
    /// </summary>
    private static EmailHtmlBuilder WithBookingLink(EmailHtmlBuilder builder, string? bookingUrl) =>
        string.IsNullOrWhiteSpace(bookingUrl)
            ? builder
            : builder.Button("HostBookingConfirmed_Cta", bookingUrl).LinkFallback("Booking_LinkFallback", bookingUrl);

    /// <summary>
    /// Push text of an Alloggiati stay alert: property name and check-in date. The check-out reminder has its own text
    /// (<see cref="CheckoutReminderPush"/>).
    /// </summary>
    public static PushText StayAlertPush(CultureInfo culture, StayAlertKind kind, string propertyName, DateTime checkInDate)
    {
        var prefix = kind switch
        {
            StayAlertKind.GuestDataMissing => "StayAlertPush_GuestDataMissing",
            StayAlertKind.AlloggiatiDeadlineApproaching => "StayAlertPush_DeadlineApproaching",
            StayAlertKind.AlloggiatiOverdue => "StayAlertPush_Overdue",
            StayAlertKind.AlloggiatiFailed => "StayAlertPush_Failed",
            StayAlertKind.ReceiptMissing => "StayAlertPush_ReceiptMissing",
            StayAlertKind.ShortStayAtRisk => "StayAlertPush_ShortStayAtRisk",
            StayAlertKind.OverThirtyNights => "StayAlertPush_OverThirtyNights",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No push text for this alert."),
        };
        var date = checkInDate.ToString(EmailTexts.Get("Format_Date", culture), culture);
        return new PushText(
            EmailTexts.Get($"{prefix}_Title", culture),
            string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), propertyName, date));
    }
}
