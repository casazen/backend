using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>«An item waits for your approval», to the host, when the preferences of the host ask for an email (E05/F04, NF-68).</summary>
        public const string HostApprovalToDecide = "host-approval-to-decide";
    }

    /// <summary>
    /// The email to a host when an item of the approval queue waits for a decision (E05/F04/US01/T02, NF-68): the draft of a reply to a guest, a
    /// campaign. It is sent once for each item and person. <b>It never carries the text of the item, the name of a guest or any address</b>: it names the
    /// property when there is one and sends the host to the queue in CasaZen, where the text is read, edited, approved or rejected.
    /// </summary>
    /// <param name="propertyName">The property the item concerns; <c>null</c> for an item of the org (a campaign).</param>
    /// <param name="approvalsUrl">The queue in the host console; <c>null</c> when the public URL is not configured (no button then).</param>
    public static EmailContent HostApprovalToDecide(CultureInfo culture, string? propertyName, string? approvalsUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        if (string.IsNullOrWhiteSpace(propertyName))
            builder.Paragraph("HostApprovalToDecide_Body");
        else
            builder.Paragraph("HostApprovalToDecide_BodyProperty", propertyName);

        builder.Paragraph("HostApprovalToDecide_How");
        if (approvalsUrl is not null)
        {
            builder.Button("HostApprovalToDecide_Cta", approvalsUrl);
            builder.LinkFallback("HostApprovalToDecide_LinkFallback", approvalsUrl);
        }

        return builder
            .Muted("HostApprovalToDecide_Why")
            .Build("HostApprovalToDecide_Subject");
    }

    /// <summary>
    /// Push text of an item of the approval queue that waits for a decision, to a host (E05/F04/US01/T02): the lock screen says that a draft reply (or
    /// something else) is waiting and, when there is one, for which property. Never the text of the item, the name of a guest or an address.
    /// </summary>
    /// <param name="messageDraft"><c>true</c> for the draft of a message to a guest («Bozza pronta»), <c>false</c> for every other item.</param>
    /// <param name="propertyName">The property the item concerns; <c>null</c> for an item of the org.</param>
    public static PushText ApprovalPush(CultureInfo culture, bool messageDraft, string? propertyName)
    {
        var kind = messageDraft ? "Push_ApprovalDraft" : "Push_ApprovalItem";
        var body = string.IsNullOrWhiteSpace(propertyName)
            ? EmailTexts.Get($"{kind}_Body", culture)
            : string.Format(culture, EmailTexts.Get($"{kind}Property_Body", culture), propertyName);
        return new PushText(EmailTexts.Get($"{kind}_Title", culture), body);
    }
}
