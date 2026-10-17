using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The host's side of the verified reviews (DB-12a): the reviews of its properties, the one answer it may give to each, and the report
/// to CasaZen. There is no way to hide or delete a review: only the staff takes one down, after a report. Reading needs
/// <c>booking.read</c>, answering and reporting <c>booking.write</c>; what a caller sees follows its <see cref="HostScope"/> (in SQL) and
/// a review of a property it does not reach answers 404, like a missing one.
/// </summary>
[ApiController]
[Route("api/reviews")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class ReviewsController(
    IHostReviewService reviews,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// The reviews of the properties the caller reaches, newest first, a page at a time, optionally those of one property. They carry
    /// their state: <c>Published</c>, <c>Reported</c> (reported by the host, still public) or <c>Removed</c> (taken down by the staff).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<HostReviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<HostReviewDto>>> List(
        [FromQuery] Guid? propertyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ReviewRules.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await reviews.ListAsync(scope, propertyId, page, pageSize, cancellationToken);
        return Ok(new PagedResultDto<HostReviewDto>
        {
            Items = result.Items.Select(HostReviewDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }

    /// <summary>
    /// The answer of the host to a review, once, as written (at most 800 characters). 409 <c>review_reply_already_sent</c> when it
    /// already answered; 422 <c>review_reply_invalid</c> for an empty text.
    /// </summary>
    [HttpPost("{id:guid}/reply")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(HostReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostReviewDto>> Reply(Guid id, [FromBody] ReplyReviewRequest request, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return NotFound();

        return Ok(HostReviewDto.From(await reviews.ReplyAsync(id, request.Text, cancellationToken)));
    }

    /// <summary>
    /// The report of a review to CasaZen, with a reason from the list and a note for the staff. The review stays public until the staff
    /// decides. 409 <c>review_already_reported</c>: one report per review.
    /// </summary>
    [HttpPost("{id:guid}/report")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(HostReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostReviewDto>> Report(Guid id, [FromBody] ReportReviewRequest request, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return NotFound();

        var reported = await reviews.ReportAsync(id, request.Reason!.Value, request.Note, User.GetUserId(), cancellationToken);
        return Ok(HostReviewDto.From(reported));
    }

    // The review is authorized as a row of its property: org, permission and the caller's reach on the property.
    private async Task<bool> CanWriteAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var propertyId = await reviews.FindPropertyIdAsync(reviewId, cancellationToken);
        var resource = propertyId is null ? null : await hostResources.ForPropertyAsync(propertyId.Value, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write);
    }
}
