using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The draft of the reply to a review (E05/F06/US01/T03, NF-69, AI-06): a person asks the assistant for a draft now. Behind <c>Features:MessageAssistant</c>: 404 while it is off, before
/// authentication. Every answer is private and not cached. <b>Nothing is sent from here</b>: the draft goes to the approval queue (<c>ReviewReply</c>) and the reply reaches the channel, or the booking
/// site, only when a person approves the item.
/// </summary>
/// <remarks>
/// A draft now needs <see cref="CasazenPolicies.BookingWrite"/> and the row check of the review (the caller's <see cref="HostScope"/> in SQL: a review of another org, or out of the reach of a
/// collaborator «Solo alcuni», is the 404 of a review that does not exist). A draft that waits in the queue for the same review is superseded by the new one (rejected with the reason
/// <c>regenerated</c>), in the reach of the caller.
/// </remarks>
[ApiController]
[Route("api/reviews")]
[FeatureGate(FeatureFlags.MessageAssistant)]
[PrivateAnswer]
public sealed class ReviewDraftsController(
    IReviewReplyDrafter drafter,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// A draft now of the reply to a review (the key the hub gave: <c>otareview:{id}</c> for a channel, <c>review:{id}</c> for the booking site): the model writes it from the words and the stars of
    /// the review, without the name of the guest, in the language of the review and with the tone and the signature of the rules of the property; the answer says whether it is in the queue.
    /// </summary>
    /// <response code="200">The outcome: <c>Drafted</c> (with the item of the queue) or <c>NotDrafted</c> (with its <c>reason</c>).</response>
    /// <response code="400"><c>thread_key_invalid</c>.</response>
    /// <response code="404"><c>review_not_found</c>: not in the caller's org or reach, or the key of a conversation.</response>
    /// <response code="429"><c>rate_limited</c>: the limits of the AI endpoints.</response>
    [HttpPost("{reviewKey}/draft")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [AiRateLimit]
    [ProducesResponseType(typeof(ReviewDraftOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ReviewDraftOutcomeDto>> Draft(string reviewKey, CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(reviewKey, out var key))
            return this.ApiProblem(StatusCodes.Status400BadRequest, MessageThreadKey.InvalidCode, "ThreadKeyInvalid");

        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");
        }

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var outcome = await drafter.DraftAsync(
            orgId,
            key,
            new ReviewDraftOptions { RequestedBy = scope, RequestedByUserId = userId },
            cancellationToken);

        return outcome.Status == ReviewDraftStatus.ReviewNotFound
            ? this.ApiProblem(StatusCodes.Status404NotFound, "review_not_found", "OtaReviewNotFound")
            : Ok(ReviewDraftOutcomeDto.From(outcome));
    }
}
