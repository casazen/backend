using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/campaigns/studio")]
[FeatureGate(FeatureFlags.CrmMarketing)]
[Authorize(Policy = CasazenPolicies.Marketing)]
public sealed class MarketingStudioController(MarketingStudio studio, AppDbContext db, IOrgContextResolver orgs,
    IHostScopeResolver scopes, TimeProvider clock) : ControllerBase
{
    private async Task<IActionResult> WithinScope(Func<HostScope, Task<IActionResult>> action)
    {
        var ct = HttpContext.RequestAborted;
        var org = await orgs.GetOrProvisionOrgIdAsync(ct);
        if (org is null) return Unauthorized();
        var scope = await scopes.ResolveHostScopeAsync(User, org.Value, ct);
        return scope is null ? Forbid() : await action(scope);
    }

    [HttpGet("settings")]
    public Task<IActionResult> GetSettings(CancellationToken ct) =>
        WithinScope(async scope => Ok(await studio.SettingsAsync(scope, ct)));

    [HttpPut("settings")]
    public Task<IActionResult> SaveSettings(StudioSettings settings, CancellationToken ct) =>
        WithinScope(async scope =>
        {
            if (!scope.IsOrgWide) return Forbid();
            await studio.SaveSettingsAsync(scope, settings, ct);
            return Ok(await studio.SettingsAsync(scope, ct));
        });

    [HttpPost("events")]
    [AiRateLimit]
    public Task<IActionResult> Events(EventRequest request, CancellationToken ct) =>
        WithinScope(async scope => Ok(await studio.EventsAsync(scope, request, ct)));

    [HttpPost("generate")]
    [AiRateLimit]
    public Task<IActionResult> Generate(StudioBrief brief, CancellationToken ct) =>
        WithinScope(async scope => Ok(ViewAsset(await studio.GenerateAsync(scope, brief, ct))));

    public record VoiceExamples(string? Examples, string? Website);
    public record SegmentBrief(string Brief);

    [HttpPost("suggest-voice")]
    [AiRateLimit]
    public Task<IActionResult> SuggestVoice(VoiceExamples request, CancellationToken ct) =>
        WithinScope(async scope => Ok(new { voice = await studio.SuggestVoiceAsync(scope, request.Examples, request.Website, ct) }));

    [HttpPost("suggest-segment")]
    [AiRateLimit]
    public Task<IActionResult> SuggestSegment(SegmentBrief request, CancellationToken ct) =>
        WithinScope(async scope => Ok(await studio.SuggestSegmentAsync(scope, request.Brief, ct)));

    [HttpGet("assets")]
    public Task<IActionResult> Assets(CancellationToken ct) => WithinScope(async scope =>
    {
        var rows = await studio.Assets(scope).AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(200).ToListAsync(ct);
        return Ok(rows.Select(ViewAsset));
    });

    [HttpPost("assets/{id:guid}/approve")]
    public Task<IActionResult> Approve(Guid id, CancellationToken ct) => WithinScope(async scope =>
    {
        var asset = await studio.Assets(scope).SingleOrDefaultAsync(a => a.Id == id, ct);
        if (asset == null) return NotFound();
        if (asset.Channel is "email" or "newsletter") return Conflict();
        asset.ApprovedAt ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return Ok(ViewAsset(asset));
    });

    [HttpPost("assets/{id:guid}/draft")]
    public Task<IActionResult> Draft(Guid id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] StudioEdit? edit, CancellationToken ct) =>
        WithinScope(async scope => Ok(new { id = await studio.DraftAsync(scope, id, edit, ct) }));

    [HttpGet("assets/{id:guid}/results")]
    public Task<IActionResult> Results(Guid id, CancellationToken ct) => WithinScope(async scope =>
    {
        var asset = await studio.Assets(scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        if (asset == null) return NotFound();
        var key = asset.Id.ToString("D");
        var revenue = await db.Bookings.Where(b => !asset.ExternalDestination && b.OrgId == scope.OrgId && b.PropertyId == asset.PropertyId &&
            b.AttributionCampaign == key && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut))
            .GroupBy(b => b.Property.Currency).Select(g => new { currency = g.Key, revenue = g.Sum(b => b.TotalPrice), count = g.Count() }).ToListAsync(ct);
        return Ok(new { asset.Clicks, bookings = revenue.Sum(g => g.count), revenueByCurrency = revenue, asset.ExternalDestination });
    });

    private object ViewAsset(MarketingAsset a) => new { a.Id, a.PropertyId, a.SegmentId, a.CampaignId, a.Channel, a.Subject,
        a.Body, a.Keywords, a.Audience, a.Budget, a.ApprovedAt, a.CreatedAt, trackingUrl = studio.TrackingUrl(a) };
}

[ApiController]
[Route("api/marketing/visit")]
public sealed class MarketingVisitController(AppDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Visit(Guid id, CancellationToken ct)
    {
        var query = db.MarketingAssets.IgnoreQueryFilters().Where(a => a.Id == id && a.Destination != null &&
            (a.ApprovedAt != null || db.Campaigns.IgnoreQueryFilters().Any(c => c.Id == a.CampaignId && c.OrgId == a.OrgId &&
                (c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.Sent || c.Status == CampaignStatus.Approved))));
        var asset = await query.AsNoTracking().SingleOrDefaultAsync(ct);
        if (asset == null || !MarketingStudio.IsPublicUrl(asset.Destination)) return NotFound();
        await query.ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Clicks, a => a.Clicks + 1), ct);
        return Redirect(MarketingStudio.TaggedDestination(asset));
    }
}
