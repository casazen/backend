using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The e-mails of the owner portal operations (E07/F07/US01/T02): the invitation of a delegated contact and the notices of a new message of the owner inbox.
/// The notices say that there is a message and link back to CasaZen: <b>never the words of the message, an amount, a statement figure or a guest</b>
/// (AC-NF111-5); the name of the agency is the only value they carry.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string OwnerContactInvitation = "owner-contact-invitation";
        public const string OwnerInboxMessage = "owner-inbox-message";
        public const string OwnerInboxMessageToAgency = "owner-inbox-message-to-agency";
    }

    /// <summary>The invitation of a contact an owner lets into its portal: who invited, for which agency, the link and when it lapses.</summary>
    public static EmailContent OwnerContactInvitation(
        CultureInfo culture,
        string contactName,
        string ownerName,
        string agencyName,
        string acceptUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("OwnerContactInvitation_Greeting", contactName)
            .Paragraph("OwnerContactInvitation_Body", ownerName, agencyName)
            .Button("OwnerContactInvitation_Cta", acceptUrl)
            .LinkFallback("OwnerContactInvitation_LinkFallback", acceptUrl)
            .Muted("OwnerContactInvitation_Expires", builder.FormatInstant(expiresAtUtc))
            .Build("OwnerContactInvitation_Subject", agencyName);
    }

    /// <summary>The owner side is told that the agency wrote: the agency and the link to the conversation, nothing of the message.</summary>
    public static EmailContent OwnerInboxMessage(CultureInfo culture, string agencyName, string conversationUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("OwnerInboxMessage_Body", agencyName)
            .Button("OwnerInboxMessage_Cta", conversationUrl)
            .LinkFallback("OwnerInboxMessage_LinkFallback", conversationUrl)
            .Muted("OwnerInboxMessage_Preferences")
            .Build("OwnerInboxMessage_Subject", agencyName);

    /// <summary>The agency is told that an owner wrote: the link to the conversation, nothing of the message and no name.</summary>
    public static EmailContent OwnerInboxMessageToAgency(CultureInfo culture, string conversationUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("OwnerInboxToAgency_Body")
            .Button("OwnerInboxToAgency_Cta", conversationUrl)
            .LinkFallback("OwnerInboxToAgency_LinkFallback", conversationUrl)
            .Build("OwnerInboxToAgency_Subject");
}
