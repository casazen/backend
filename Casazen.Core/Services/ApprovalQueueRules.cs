using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public static class ApprovalItemTypes
{
    public const string Campaign = "Campaign";

    public const string MessageDraft = "MessageDraft";

    public const string ReviewReply = "ReviewReply";

    public const string Routine = "Routine";

    public const string AiText = "AiText";
}

public static class ApprovalOriginActions
{
    public const string SendGuestMessage = "SendGuestMessage";

    public const string ApproveCampaign = "ApproveCampaign";

    public const string SendReviewReply = "SendReviewReply";

    public const string None = "None";
}

public static class ApprovalQueueRules
{
    public const string PortalLocalDraftVersion = "portal-local-v1";

    public const int ExpirationDays = 7;

    public const int ResubmitWindowDays = 7;

    /// <summary>
    /// Days the words of an item (its text, the host's edit of it and the reason of a rejection) are kept after the item was decided or expired: then the daily job empties them
    /// (<c>ApprovalExpirationJob</c>); the decision, its author, its time and the action stay. A documented constant and not a setting: the queue is working data, short-lived by
    /// design (an item waits seven days), and the message it carries is a copy of the one of the conversations, whose own retention is <c>Gdpr:Retention:AlloggiatiData</c> (docs/runbooks/gdpr.md § 14).
    /// The erasure of a guest empties the items of its conversations at once, with no wait.
    /// </summary>
    public const int TextRetentionDays = 30;

    public const string PushCategory = "draft-ready";

    public const string EditInvalidCode = "approval_edit_invalid";

    public const string RejectionReasonRequiredCode = "approval_rejection_reason_required";

    public const string OriginActionFailedCode = "approval_origin_action_failed";

    public const string NotPendingCode = "approval_item_not_pending";

    public const string NotFoundCode = "approval_item_not_found";

    public const string ExpiredReason = "expired";

    public const string UnexpectedErrorCode = "origin_action_error";

    public const int CampaignSubjectMaxLength = 200;

    public const int MaxSendAttempts = 10;

    public const string SendAttemptsExhaustedCode = "approval_send_attempts_exhausted";

    /// <summary>
    /// The key a message sends with (<c>IMessageReplyService.ReplyAsync</c>): one per item and text, so a second approval of the same item (a double click, two
    /// hosts, a retry after a timeout) reaches the guest once, and a text the host approves later (the item went back to pending with a new one, or it was edited after a
    /// refusal) is not taken for the message that went before: the service of the replies replays the message of a key it already knows, whatever text it is given now. It
    /// follows the rule of <see cref="GuestMessageRules.NormalizeIdempotencyKey"/>: only letters, digits, dashes and underscores (a colon would be refused as
    /// <c>idempotency_key_invalid</c>), 8 to 64 characters (at most 57 here).
    /// </summary>
    /// <param name="itemId">The item of the queue.</param>
    /// <param name="finalText">The text that is sent (the edited one when there is one): the first eight hexadecimal digits of its SHA-256 are part of the key.</param>
    /// <param name="attempt">
    /// 1 for the first send (<c>approval-{id}-{hash}</c>). A message the channel manager <b>refused</b> did not go out, but the same key would replay that refusal
    /// for ever: the action sends again with the key of the next attempt (<c>approval-{id}-{hash}-2</c>, ...), which only happens after a refusal, never after a send
    /// whose outcome is not known (that one is the same message, checked on the channel).
    /// </param>
    public static string SendKeyOf(Guid itemId, string finalText, int attempt = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalText);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(finalText)).AsSpan(0, 4));
        var key = $"approval-{itemId:D}-{hash}";
        return attempt == 1 ? key : $"{key}-{attempt}";
    }

    /// <summary>
    /// The instant an alert was raised as it is written in the keys of the push, of the bell and of the e-mail slot: UTC, whole seconds, <c>yyyyMMddHHmmss</c>, with the invariant
    /// culture (a custom format of a <see cref="DateTime"/> takes its calendar from the culture, so a machine with another culture would write another key). Two alerts of one item a second
    /// apart are two alerts; an alert raised again for the same moment has the same stamp.
    /// </summary>
    public static string StampOf(DateTime raisedAtUtc) =>
        UtcDateTime.Normalize(raisedAtUtc).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public static string? NormalizeEditedText(string itemType, string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (string.Equals(itemType, ApprovalItemTypes.MessageDraft, StringComparison.Ordinal))
        {
            return GuestMessageRules.NormalizeText(text) is { Length: <= GuestMessageRules.TextMaxLength } message ? message : null;
        }

        if (string.Equals(itemType, ApprovalItemTypes.Campaign, StringComparison.Ordinal))
        {
            return TryParseCampaignText(text, out var subject, out var body) ? CampaignTextOf(subject, body) : null;
        }

        if (string.Equals(itemType, ApprovalItemTypes.ReviewReply, StringComparison.Ordinal))
        {
            return ReviewRules.NormalizeText(text) is { Length: <= ReviewRules.ReplyMaxLength } reply ? reply : null;
        }

        return text.Trim();
    }

    public static bool TryParseCampaignText(string? text, out string subject, out string body)
    {
        subject = string.Empty;
        body = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        var split = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        if (split <= 0)
            return false;

        var candidateSubject = normalized[..split].Trim();
        var candidateBody = normalized[(split + 2)..].Trim();
        if (candidateSubject.Length is 0 or > CampaignSubjectMaxLength
            || candidateSubject.Contains('\n', StringComparison.Ordinal)
            || candidateBody.Length == 0)
        {
            return false;
        }

        subject = candidateSubject;
        body = candidateBody;
        return true;
    }

    public static string CampaignTextOf(string subject, string body) => $"{subject}\n\n{body}";

    public static string? NormalizeRejectionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        var normalized = reason.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return normalized.Length <= ApprovalQueueItem.RejectionReasonMaxLength ? normalized : null;
    }
}
