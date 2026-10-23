using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>Reminder to the landlord of the last day to give notice of a long-term lease (LR-11c-a).</summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string LeaseNoticeReminder = "lease-notice-reminder";
    }

    /// <summary>
    /// The last useful day to give notice of a lease is coming (or is today), to the landlord: the day, the days left, when the current
    /// period ends and for how many months the lease renews by itself if nobody gives notice (<paramref name="renewalMonths"/>, <c>null</c>
    /// for a type that does not renew: the paragraph is then left out). It names the property and nothing of the tenant, and shows no
    /// amount. CasaZen works the day out from the end of the current period, does not send the notice and does not judge its validity: the
    /// email says so and sends the landlord back to the notice period written in the contract (decision LR-D29).
    /// </summary>
    public static EmailContent LeaseNoticeReminder(
        CultureInfo culture,
        string propertyName,
        DateOnly lastDayToGiveNotice,
        int daysRemaining,
        DateOnly currentTermEnd,
        int? renewalMonths)
    {
        var builder = new EmailHtmlBuilder(culture);
        if (daysRemaining <= 0)
            builder.Paragraph("LeaseNoticeReminder_BodyToday", propertyName, AsDate(lastDayToGiveNotice));
        else
            builder.Paragraph("LeaseNoticeReminder_Body", propertyName, AsDate(lastDayToGiveNotice), daysRemaining);

        if (renewalMonths is { } months)
            builder.Paragraph("LeaseNoticeReminder_Renewal", AsDate(currentTermEnd), months);

        return builder
            .Paragraph("LeaseNoticeReminder_Responsibility")
            .Build("LeaseNoticeReminder_Subject", propertyName, AsDate(lastDayToGiveNotice));
    }
}
