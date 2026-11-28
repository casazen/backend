using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The assistant writes the reply to a review for the host to approve (E05/F06/US01/T03, NF-69, AI-06, PO-09): a draft of the public reply, in the language of the review, with the tone and
/// the signature of the effective rules of the property, put in the approval queue (<see cref="ApprovalItemTypes.ReviewReply"/>, origin action
/// <see cref="ApprovalOriginActions.SendReviewReply"/>). <b>Never automatic</b>: a reply to a review is public, so nothing here sends it; the reply reaches the channel (or the booking
/// site) only through the approve action of the queue, after a person approved it (the auto-send of the assistant is deferred, block B01).
/// </summary>
/// <remarks>
/// <para><b>Personal data (PO-09).</b> The review goes to the model through <c>AssistantPromptBuilder.Minimize</c>: the names of the guest and of the host, the codes of the booking, the
/// contact data and the long numbers never reach it, and the reply is written without a name (it does not greet by one). The log keeps ids and codes, never a word of the review or of the
/// draft.</para>
/// <para><b>What a reply never says.</b> The instructions forbid a promise of money (a refund, a compensation, a discount, a gift), a mention of being an AI and any link or contact
/// detail; the server checks the answer for the same (<see cref="ReviewReplyViolations"/>) and a reply that breaks one is no draft: nothing is queued and the reason is logged.</para>
/// <para><b>Tenant.</b> Every method names the org; the draft a person asks for is narrowed to the reach of the caller in SQL (<see cref="HostScope"/>). The service checks no role.</para>
/// </remarks>
public interface IReviewReplyDrafter
{
    Task<ReviewDraftOutcome> DraftAsync(Guid orgId, MessageThreadKey reviewKey, ReviewDraftOptions? options = null, CancellationToken ct = default);
}

public sealed record ReviewDraftOptions
{
    public static ReviewDraftOptions Automatic { get; } = new();

    public HostScope? RequestedBy { get; init; }

    public string? RequestedByUserId { get; init; }

    public bool Requested => RequestedBy is not null;
}

public enum ReviewDraftStatus
{
    Drafted = 0,

    NotDrafted = 1,

    ReviewNotFound = 2,
}

public sealed record ReviewDraftOutcome(ReviewDraftStatus Status, Guid? ApprovalItemId = null, string? Reason = null)
{
    public static ReviewDraftOutcome Drafted(Guid approvalItemId) => new(ReviewDraftStatus.Drafted, approvalItemId);

    public static ReviewDraftOutcome NotDrafted(string reason) => new(ReviewDraftStatus.NotDrafted, null, reason);

    public static ReviewDraftOutcome NotFound { get; } = new(ReviewDraftStatus.ReviewNotFound);
}

public static class ReviewDraftReasons
{
    public const string AssistantDisabled = AssistantDecisionCodes.AssistantDisabled;

    public const string AiProviderNotConfigured = AssistantDecisionCodes.AiProviderNotConfigured;

    public const string AiBudgetExceeded = AssistantDecisionCodes.AiBudgetExceeded;

    public const string AiProviderError = AssistantDecisionCodes.AiProviderError;

    public const string AiInvalidAnswer = AssistantDecisionCodes.AiInvalidAnswer;

    public const string DraftRuleViolation = "draft_rule_violation";

    public const string AlreadyAnswered = "already_answered";

    public const string AlreadyDrafted = "already_drafted";

    public const string NotPublished = OtaReviewErrorCodes.NotPublished;

    public const string ChannelNotSupported = OtaReviewErrorCodes.ChannelNotSupported;

    public const string ReplyNotPossible = "reply_not_possible";
}
