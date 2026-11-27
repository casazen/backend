using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>«A guest wrote on a channel», to the host, when the preferences of the host ask for an email (E05/F03, NF-65).</summary>
        public const string HostMessageReceived = "host-message-received";
    }

    /// <summary>
    /// The email to a host when a guest writes in a conversation of a channel (E05/F03/US01/T03, NF-65). It is the consequence of that message
    /// and is sent at most once every ten minutes per conversation and person. <b>It never carries the text of the message, the name of the guest
    /// or any address of the guest</b>: it names the channel and the property and sends the host to the conversation in CasaZen.
    /// </summary>
    /// <param name="propertyName">The property of the conversation; <c>null</c> while the listing of the conversation is not mapped to one.</param>
    /// <param name="channelName">The channel, a brand name (<see cref="OtaChannelBrand"/>).</param>
    /// <param name="threadUrl">The conversation in the host console; <c>null</c> when the public URL is not configured (no button then).</param>
    public static EmailContent HostMessageReceived(CultureInfo culture, string? propertyName, string channelName, string? threadUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        if (string.IsNullOrWhiteSpace(propertyName))
            builder.Paragraph("HostMessageReceived_BodyUnassigned", channelName);
        else
            builder.Paragraph("HostMessageReceived_Body", channelName, propertyName);

        builder.Paragraph("HostMessageReceived_How");
        if (threadUrl is not null)
        {
            builder.Button("HostMessageReceived_Cta", threadUrl);
            builder.LinkFallback("HostMessageReceived_LinkFallback", threadUrl);
        }

        return builder
            .Muted("HostMessageReceived_Why")
            .Build("HostMessageReceived_Subject", string.IsNullOrWhiteSpace(propertyName) ? channelName : propertyName);
    }

    /// <summary>
    /// Push text of a message of a guest on a channel, to a host (E05/F03/US01/T03): the property and the channel only. Neither the guest's name
    /// nor the text of the message reach the lock screen.
    /// </summary>
    public static PushText OtaMessagePush(CultureInfo culture, string? propertyName, string channelName) =>
        string.IsNullOrWhiteSpace(propertyName)
            ? new PushText(
                EmailTexts.Get("Push_OtaMessage_Title", culture),
                string.Format(culture, EmailTexts.Get("Push_OtaMessageUnassigned_Body", culture), channelName))
            : new PushText(
                EmailTexts.Get("Push_OtaMessage_Title", culture),
                string.Format(culture, EmailTexts.Get("Push_OtaMessage_Body", culture), propertyName, channelName));

    /// <summary>The name of a channel of the hub of messages as the host knows it. Brand names, the same in every language.</summary>
    public static string OtaChannelBrand(Casazen.Core.Entities.Enums.OtaChannel channel) => channel switch
    {
        Casazen.Core.Entities.Enums.OtaChannel.Airbnb => "Airbnb",
        Casazen.Core.Entities.Enums.OtaChannel.Booking => "Booking.com",
        Casazen.Core.Entities.Enums.OtaChannel.Vrbo => "Vrbo",
        Casazen.Core.Entities.Enums.OtaChannel.Expedia => "Expedia",
        _ => channel.ToString(),
    };
}
