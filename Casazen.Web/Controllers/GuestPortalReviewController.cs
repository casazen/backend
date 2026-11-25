using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalReviewController(IPublicReviewService reviews, AppDbContext db) : GuestPortalControllerBase
{
    [HttpGet("review")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!await ReviewsEnabled(ct)) return NotFound();

        var info = await reviews.GetPortalReviewAsync(Portal, ct);
        return Ok(new { submitted = info.Review is not null });
    }

    [HttpPost("review")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Submit(ReviewRequest request, CancellationToken ct)
    {
        if (!await ReviewsEnabled(ct)) return NotFound();

        var review = await reviews.SubmitPortalReviewAsync(Portal,
            new SubmitReviewCommand(request.Rating, request.Text, request.ShowName), ct);
        return Ok(PublicReviewDto.From(review));
    }

    private async Task<bool> ReviewsEnabled(CancellationToken ct)
    {
        var json = await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => p.Id == Portal.PropertyId && p.OrgId == Portal.OrgId)
            .Select(p => p.GuestPortalSetupJson).SingleOrDefaultAsync(ct);
        return GuestPortalSetup.Read(json).ReviewsEnabled is not false;
    }

    public sealed record ReviewRequest(
        [property: Range(ReviewRules.MinRating, ReviewRules.MaxRating)] int Rating,
        [property: MaxLength(ReviewRules.TextMaxLength)] string? Text,
        bool ShowName);
}
