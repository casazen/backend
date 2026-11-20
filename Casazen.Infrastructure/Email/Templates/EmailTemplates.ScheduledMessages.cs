using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>«A scheduled message needs your attention», to the host, when the preferences of the host ask for an email (E05/F07/US01, NF-71).</summary>
        public const string HostScheduledMessageIssue = "host-scheduled-message-issue";
    }

    /// <summary>
    /// The email to a host when a scheduled message of a booking did not go out, went out at an unusual hour or was skipped by the system (E05/F07/US01, NF-71, AC-NF71-3). It is sent once for each
    /// message and kind of problem and person. <b>It never carries the words of the message, the name of a guest, a link, a code or any address</b>: it names the property when there is one and sends the host
    /// to the booking in CasaZen, where the message is read, edited, skipped or sent.
    /// </summary>
    /// <param name="propertyName">The property of the booking; <c>null</c> when it is gone.</param>
    /// <param name="bookingUrl">The booking in the host console; <c>null</c> when the public URL is not configured (no button then).</param>
    public static EmailContent HostScheduledMessageIssue(CultureInfo culture, string? propertyName, string? bookingUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        if (string.IsNullOrWhiteSpace(propertyName))
            builder.Paragraph("HostScheduledMessageIssue_Body");
        else
            builder.Paragraph("HostScheduledMessageIssue_BodyProperty", propertyName);

        builder.Paragraph("HostScheduledMessageIssue_How");
        if (bookingUrl is not null)
        {
            builder.Button("HostScheduledMessageIssue_Cta", bookingUrl);
            builder.LinkFallback("HostScheduledMessageIssue_LinkFallback", bookingUrl);
        }

        return builder
            .Muted("HostScheduledMessageIssue_Why")
            .Build("HostScheduledMessageIssue_Subject");
    }

    /// <summary>
    /// Push text of a scheduled message that needs the host (E05/F07/US01, NF-71): the lock screen says that a scheduled message did not go out (or went out at an unusual hour) and, when there is one,
    /// for which property. Never the words of the message, the name of a guest, a link, a code or an address.
    /// </summary>
    /// <param name="propertyName">The property of the booking; <c>null</c> when it is gone.</param>
    public static PushText ScheduledMessageIssuePush(CultureInfo culture, string? propertyName)
    {
        var body = string.IsNullOrWhiteSpace(propertyName)
            ? EmailTexts.Get("Push_ScheduledMessageIssue_Body", culture)
            : string.Format(culture, EmailTexts.Get("Push_ScheduledMessageIssueProperty_Body", culture), propertyName);
        return new PushText(EmailTexts.Get("Push_ScheduledMessageIssue_Title", culture), body);
    }
}
