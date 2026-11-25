using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalReviewController(IPublicReviewService reviews) : GuestPortalControllerBase
{
    [HttpGet("review")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var info = await reviews.GetPortalReviewAsync(Portal, ct);
        return Ok(new { submitted = info.Review is not null });
    }

    [HttpPost("review")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Submit(ReviewRequest request, CancellationToken ct)
    {
        var review = await reviews.SubmitPortalReviewAsync(Portal,
            new SubmitReviewCommand(request.Rating, request.Text, request.ShowName), ct);
        return Ok(PublicReviewDto.From(review));
    }

    public sealed record ReviewRequest(
        [property: Range(ReviewRules.MinRating, ReviewRules.MaxRating)] int Rating,
        [property: MaxLength(ReviewRules.TextMaxLength)] string? Text,
        bool ShowName);
}
