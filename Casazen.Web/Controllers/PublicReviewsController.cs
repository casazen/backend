using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The verified reviews on the public booking site (DB-12a): the reviews of a property, and the page behind the link the host's email
/// carries. Anonymous, rate limited per client IP, never indexable on the link side. The review is published when the guest sends it;
/// nothing is written to the guest.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicReviewsController(IOrgService orgService, IPublicReviewService reviews) : ControllerBase
{
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>
    /// The public reviews of a property of the site, newest first, a page at a time: the first name of the guest (or «Ospite»), the mark,
    /// the text, the month of the stay and the answer of the host. Nothing else of the guest. A removed review is never listed; a
    /// reported one is, until the staff decides. 404 for an org or a property that is not on the site. The count and the average are in
    /// <c>ratingSummary</c> of the property (absent when there is no review).
    /// </summary>
    [HttpGet("orgs/{slug}/properties/{propertySlugOrId}/reviews")]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
    [ProducesResponseType(typeof(PagedResultDto<PublicReviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<PublicReviewDto>>> List(
        [StringLength(100)] string slug,
        [StringLength(100)] string propertySlugOrId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ReviewRules.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var result = await reviews.ListAsync(org.Id, propertySlugOrId, page, pageSize, cancellationToken);
        if (result is null)
            return NotFound();

        return Ok(new PagedResultDto<PublicReviewDto>
        {
            Items = result.Items.Select(PublicReviewDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }

    /// <summary>
    /// What the page of the review link shows: the property, the dates and, when the guest already wrote it, the review. The token of the
    /// email is in the body, not in the address. 422 <c>review_link_invalid</c> for a link that is not ours, is expired or is for another
    /// booking (the same answer for all); 409 <c>review_not_available</c> when the stay can no longer be reviewed.
    /// </summary>
    [HttpPost("bookings/{bookingId:guid}/review/lookup")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [RequestSizeLimit(MaxBodyBytes)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(ReviewLinkInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ReviewLinkInfoDto>> Lookup(
        Guid bookingId,
        [FromBody] ReviewLinkRequest request,
        CancellationToken cancellationToken)
    {
        var info = await reviews.GetLinkInfoAsync(bookingId, request.Token, cancellationToken);
        return Ok(ReviewLinkInfoDto.From(info));
    }

    /// <summary>
    /// The guest's review: a mark from 1 to 5, an optional text, and whether the first name may be shown. Published at once, one per
    /// booking (409 <c>review_already_submitted</c> for the second, however it comes), idempotent in effect. Same link errors as
    /// <c>lookup</c>. Answers the review as the public reads it.
    /// </summary>
    [HttpPost("bookings/{bookingId:guid}/review")]
    [EnableRateLimiting(RateLimitPolicies.PublicReviewSubmit)]
    [RequestSizeLimit(MaxBodyBytes)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PublicReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicReviewDto>> Submit(
        Guid bookingId,
        [FromBody] SubmitReviewRequest request,
        CancellationToken cancellationToken)
    {
        var review = await reviews.SubmitAsync(
            bookingId,
            request.Token,
            new SubmitReviewCommand(request.Rating, request.Text, request.ShowName),
            cancellationToken);
        return Ok(PublicReviewDto.From(review));
    }
}
