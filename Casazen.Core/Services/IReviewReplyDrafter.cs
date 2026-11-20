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
    /// <summary>
    /// Writes the draft of the reply to the review <paramref name="reviewKey"/> (<c>otareview:{id}</c> of a channel, <c>review:{id}</c> of the booking site) of <paramref name="orgId"/> and puts
    /// it in the approval queue, once: a review that already has a waiting draft (or an approved one) gets no second one from the automatic path. Without the assistant switched on for its
    /// property, an AI provider in effect or budget left there is no draft, and the reason is the outcome's (and the log's). A draft a person asked for (<paramref name="options"/>)
    /// supersedes the one that waits (rejected with the reason <see cref="AssistantDecisionCodes.RegeneratedReason"/>), in the reach of that person.
    /// </summary>
    Task<ReviewDraftOutcome> DraftAsync(Guid orgId, MessageThreadKey reviewKey, ReviewDraftOptions? options = null, CancellationToken ct = default);
}

/// <summary>How a draft is asked for: by itself (<see cref="Automatic"/>, when a review appears), or by a person who pressed «Ask the assistant for a draft».</summary>
public sealed record ReviewDraftOptions
{
    /// <summary>A draft written when the review appeared, with nobody behind it.</summary>
    public static ReviewDraftOptions Automatic { get; } = new();

    /// <summary>The reach of the person who asked: the review must be inside it (else it is not found), and the draft that waits is superseded in it. <c>null</c> for the automatic path.</summary>
    public HostScope? RequestedBy { get; init; }

    /// <summary>The person who asked (the Auth0 subject).</summary>
    public string? RequestedByUserId { get; init; }

    /// <summary>A person asked for the draft.</summary>
    public bool Requested => RequestedBy is not null;
}

/// <summary>How a request for a draft ended.</summary>
public enum ReviewDraftStatus
{
    /// <summary>A draft was written and is in the approval queue (<see cref="ReviewDraftOutcome.ApprovalItemId"/>).</summary>
    Drafted = 0,

    /// <summary>No draft was written: <see cref="ReviewDraftOutcome.Reason"/> says why (<see cref="ReviewDraftReasons"/>).</summary>
    NotDrafted = 1,

    /// <summary>The review is not there, is of another org, or is out of the reach of the person who asked.</summary>
    ReviewNotFound = 2,
}

/// <summary>What a request for a draft did: how it ended, the item of the queue when there is a draft, and the code of the reason when there is none.</summary>
public sealed record ReviewDraftOutcome(ReviewDraftStatus Status, Guid? ApprovalItemId = null, string? Reason = null)
{
    public static ReviewDraftOutcome Drafted(Guid approvalItemId) => new(ReviewDraftStatus.Drafted, approvalItemId);

    public static ReviewDraftOutcome NotDrafted(string reason) => new(ReviewDraftStatus.NotDrafted, null, reason);

    public static ReviewDraftOutcome NotFound { get; } = new(ReviewDraftStatus.ReviewNotFound);
}

/// <summary>The stable codes of the reasons a review got no draft (the answer of the API and the log): never a text.</summary>
public static class ReviewDraftReasons
{
    /// <summary>The assistant is not switched on for the property of the review (the switch of the organization, or the exception of the property).</summary>
    public const string AssistantDisabled = AssistantDecisionCodes.AssistantDisabled;

    /// <summary>No AI provider is in effect (not configured, or the stub).</summary>
    public const string AiProviderNotConfigured = AssistantDecisionCodes.AiProviderNotConfigured;

    /// <summary>The monthly budget of the AI is used up: nothing was asked of the provider.</summary>
    public const string AiBudgetExceeded = AssistantDecisionCodes.AiBudgetExceeded;

    /// <summary>The provider failed or did not answer.</summary>
    public const string AiProviderError = AssistantDecisionCodes.AiProviderError;

    /// <summary>The answer of the model was not a reply that can be used.</summary>
    public const string AiInvalidAnswer = AssistantDecisionCodes.AiInvalidAnswer;

    /// <summary>The reply of the model broke a rule (<see cref="ReviewReplyViolations"/>) or was longer than a reply can be: it is not queued.</summary>
    public const string DraftRuleViolation = "draft_rule_violation";

    /// <summary>The review was answered already, or an earlier attempt waits for a check: nothing to draft.</summary>
    public const string AlreadyAnswered = "already_answered";

    /// <summary>The review already has a draft waiting for approval (or one approved a short time ago): the automatic path makes no second one.</summary>
    public const string AlreadyDrafted = "already_drafted";

    /// <summary>The guest's review is not published yet.</summary>
    public const string NotPublished = OtaReviewErrorCodes.NotPublished;

    /// <summary>The channel of the review cannot be answered from CasaZen.</summary>
    public const string ChannelNotSupported = OtaReviewErrorCodes.ChannelNotSupported;

    /// <summary>The connector is off or the listing of the property is not decided: a reply could not be sent, so no draft is written for it.</summary>
    public const string ReplyNotPossible = "reply_not_possible";
}
