using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Reviews;

/// <summary>
/// What a request for a draft of the reply to a review did (<c>POST api/reviews/{reviewKey}/draft</c>, E05/F06/US01/T03, NF-69). <c>Status</c> is <c>Drafted</c> (the draft is in the approval queue as
/// <c>ApprovalItemId</c>, waiting for a person: nothing was sent) or <c>NotDrafted</c> with its <c>Reason</c>, a stable code: <c>assistant_disabled</c>, <c>ai_provider_not_configured</c>,
/// <c>ai_budget_exceeded</c>, <c>ai_provider_error</c>, <c>ai_invalid_answer</c>, <c>draft_rule_violation</c> (the reply the model wrote broke a rule: a promise of money, a mention of being an AI,
/// a link or a number to call), <c>draft_too_long</c>, <c>already_answered</c>, <c>ota_review_not_published</c>, <c>channel_not_integrated</c> or <c>reply_not_possible</c>. Never a text.
/// </summary>
public sealed class ReviewDraftOutcomeDto
{
    public ReviewDraftStatus Status { get; init; }
    public Guid? ApprovalItemId { get; init; }
    public string? Reason { get; init; }

    public static ReviewDraftOutcomeDto From(ReviewDraftOutcome outcome) => new()
    {
        Status = outcome.Status,
        ApprovalItemId = outcome.ApprovalItemId,
        Reason = outcome.Reason,
    };
}
