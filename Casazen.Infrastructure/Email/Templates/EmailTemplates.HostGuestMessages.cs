using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>«The host wrote to you», to the guest of a booking of a channel that has no chat, when the host writes first (E05/F02/US02/T01).</summary>
        public const string HostGuestMessage = "host-guest-message";
    }

    /// <summary>
    /// The email to the guest of a booking of a channel with no chat, when the host writes to it first (E05/F02/US02/T01, NF-63). Unlike the email that
    /// tells a guest of a direct booking that the host answered (<see cref="HostReplyToGuest"/>) there is no conversation to read it in, so the text of the
    /// host <b>is</b> in the email, HTML-encoded with its line breaks kept; every other dynamic value is encoded by the builder. The answer goes to the
    /// organization: <paramref name="replyTo"/> is its contact address (the sender stays the one CasaZen is configured with), and without it the email says to
    /// contact the property directly. The subject names the host and nothing of the guest.
    /// </summary>
    /// <param name="hostName">How the host is called: the name it chose for the site, else its display name.</param>
    /// <param name="checkInDate">The first day of the stay (a calendar date; no zone is applied).</param>
    /// <param name="checkOutDate">The last day of the stay.</param>
    /// <param name="message">What the host wrote, plain text.</param>
    /// <param name="replyTo">The address of the organization an answer goes to; <c>null</c> when it has none.</param>
    public static EmailContent HostGuestMessage(
        CultureInfo culture,
        string hostName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string message,
        string? replyTo)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("HostGuestMessage_Body", hostName, propertyName, checkInDate, checkOutDate);
        builder.Quote("HostGuestMessage_MessageLabel", message);
        if (string.IsNullOrWhiteSpace(replyTo))
            builder.Paragraph("HostGuestMessage_NoReply");
        else
            builder.Paragraph("HostGuestMessage_Reply", hostName);

        var content = builder
            .Muted("HostGuestMessage_Why")
            .Build("HostGuestMessage_Subject", hostName);
        return string.IsNullOrWhiteSpace(replyTo) ? content : content with { ReplyTo = replyTo.Trim() };
    }
}
