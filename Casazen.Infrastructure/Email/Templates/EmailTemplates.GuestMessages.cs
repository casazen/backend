using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>«The guest wrote», to the host, when the guest sends a message from the guest area (DB-14a).</summary>
        public const string GuestMessageToHost = "guest-message-to-host";

        /// <summary>«The host answered», to the guest, when the host sends a message from the booking (DB-14a).</summary>
        public const string HostReplyToGuest = "host-reply-to-guest";
    }

    /// <summary>
    /// The email to the host when the guest of a direct booking writes (DB-14a). It is the consequence of that message and is sent at most
    /// once every ten minutes per conversation. <b>It never carries the text of the message</b>: it names the guest, the property and the
    /// dates and sends the host to the booking in the app to read and answer.
    /// </summary>
    /// <param name="bookingUrl">The booking in the host console; <c>null</c> when the public URL is not configured (no button then).</param>
    public static EmailContent GuestMessageToHost(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string? bookingUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("GuestMessageToHost_Body", guestName, propertyName, checkInDate, checkOutDate);
        builder.Paragraph("GuestMessageToHost_How");
        if (bookingUrl is not null)
        {
            builder.Button("GuestMessageToHost_Cta", bookingUrl);
            builder.LinkFallback("GuestMessageToHost_LinkFallback", bookingUrl);
        }

        return builder
            .Muted("GuestMessageToHost_Why")
            .Build("GuestMessageToHost_Subject", guestName, propertyName);
    }

    /// <summary>
    /// The email to the guest when the host answers (DB-14a). It is the consequence of that answer and is sent at most once every ten
    /// minutes per conversation. <b>It never carries the text of the answer</b>: it sends the guest to its booking, which asks for the
    /// booking code and email again, as the guest area always does.
    /// </summary>
    /// <param name="guestAreaUrl">"Le mie prenotazioni" of the site, with the booking code filled in; <c>null</c> when the public URL is not configured.</param>
    public static EmailContent HostReplyToGuest(
        CultureInfo culture,
        string guestName,
        string hostName,
        string propertyName,
        string? guestAreaUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("HostReplyToGuest_Greeting", guestName);
        builder.Paragraph("HostReplyToGuest_Body", hostName, propertyName);
        builder.Paragraph("HostReplyToGuest_How");
        if (guestAreaUrl is not null)
        {
            builder.Button("HostReplyToGuest_Cta", guestAreaUrl);
            builder.LinkFallback("HostReplyToGuest_LinkFallback", guestAreaUrl);
        }

        return builder
            .Muted("HostReplyToGuest_Why")
            .Build("HostReplyToGuest_Subject", hostName);
    }
}
