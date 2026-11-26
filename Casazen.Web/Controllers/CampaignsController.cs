using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Casazen.Web.Controllers;

/// <summary>
/// Marketing campaigns and guest segments (NF-24, ADR-014).
/// Supports segment creation, campaign drafting with AI option, approval workflow, scheduling, and sending.
/// Behind <c>Features:CrmMarketing</c> (E07/F06/US01/T01): 404 while it is off, before authentication.
/// </summary>
[ApiController]
[Route("api/campaigns")]
[FeatureGate(FeatureFlags.CrmMarketing)]
[Authorize(Policy = CasazenPolicies.Marketing)]
public class CampaignsController(
    ICampaignService campaignService,
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    private async Task<IQueryable<Campaign>> AccessibleAsync(Guid orgId)
    {
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId, HttpContext.RequestAborted);
        var campaigns = db.Campaigns.Where(c => c.OrgId == orgId);
        if (scope == null) return campaigns.Where(c => false);
        if (scope.IsOrgWide) return campaigns;
        var properties = db.Properties.Where(p => p.OrgId == orgId).InScope(scope);
        return campaigns.Where(c => c.PropertyId.HasValue && properties.Any(p => p.Id == c.PropertyId));
    }

    [HttpGet("fields")]
    public IActionResult Fields() => Ok(MarketingSegmentRules.Fields.Select(f => new { f.FieldId, fieldType = f.Type.ToString(), f.DisplayNameKey, f.Operators }));

    #region Segments

    /// <summary>
    /// Creates a guest segment (AC-NF24-1).
    /// </summary>
    [HttpPost("segments")]
    [ProducesResponseType(typeof(GuestSegmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GuestSegmentDto>> CreateSegment([FromBody] CreateSegmentRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var evaluator = HttpContext.RequestServices.GetRequiredService<IConditionEvaluator>();
        var errors = evaluator.ValidateCondition(request.Condition);
        if (errors.Count > 0 || request.Condition.Groups.SelectMany(g => g.Rules).Any(r =>
            !MarketingSegmentRules.Fields.Any(f => f.FieldId == r.FieldId && f.Operators.Contains(r.Operator))))
            return BadRequest(new { error = "invalid_segment_rules", errors });

        // Serialize condition to JSON (ADR-014-R1)
        var conditionJson = JsonSerializer.Serialize(request.Condition);

        var segment = new GuestSegment
        {
            OrgId = orgId.Value,
            Name = request.Name,
            ConditionJson = conditionJson,
            CreatedAt = DateTime.UtcNow
        };

        db.GuestSegments.Add(segment);
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok(new GuestSegmentDto
        {
            Id = segment.Id,
            Name = segment.Name,
            Condition = request.Condition,
            CreatedAt = segment.CreatedAt
        });
    }

    /// <summary>
    /// Gets all segments for the org.
    /// </summary>
    [HttpGet("segments")]
    [ProducesResponseType(typeof(List<GuestSegmentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<GuestSegmentDto>>> GetSegments()
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var segments = await db.GuestSegments
            .Where(s => s.OrgId == orgId.Value)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(HttpContext.RequestAborted);

        var dtos = segments.Select(s => new GuestSegmentDto
        {
            Id = s.Id,
            Name = s.Name,
            Condition = JsonSerializer.Deserialize<Condition>(s.ConditionJson),
            CreatedAt = s.CreatedAt
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Previews recipient count for a segment (AC-NF24-3).
    /// </summary>
    [HttpGet("segments/{id}/preview")]
    [ProducesResponseType(typeof(SegmentPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SegmentPreviewDto>> PreviewSegment(Guid id)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, HttpContext.RequestAborted);
        if (scope is null)
            return Unauthorized();

        var count = await campaignService.PreviewCountAsync(id, orgId.Value, scope, HttpContext.RequestAborted);

        return Ok(new SegmentPreviewDto { Count = count });
    }

    #endregion

    #region Campaigns

    /// <summary>
    /// Creates a draft campaign (AC-NF24-4).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CampaignDto>> CreateCampaign([FromBody] CreateCampaignRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, HttpContext.RequestAborted);
        if (scope == null || (!scope.IsOrgWide && request.PropertyId == null)) return Forbid();
        if (request.PropertyId.HasValue && !await db.Properties.Where(p => p.OrgId == orgId.Value).InScope(scope)
            .AnyAsync(p => p.Id == request.PropertyId, HttpContext.RequestAborted)) return NotFound();
        var campaign = await campaignService.CreateDraftAsync(
            orgId.Value,
            request.SegmentId,
            request.Subject,
            request.Body,
            request.IsAiGenerated,
            HttpContext.RequestAborted, request.PropertyId);

        return Ok(MapToCampaignDto(campaign));
    }

    /// <summary>
    /// Gets all campaigns for the org.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<CampaignDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CampaignDto>>> GetCampaigns()
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var campaigns = await (await AccessibleAsync(orgId.Value))
            .Include(c => c.Segment)
            .Where(c => c.OrgId == orgId.Value)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(HttpContext.RequestAborted);

        var dtos = campaigns.Select(MapToCampaignDto).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Gets a single campaign.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CampaignDto>> GetCampaign(Guid id)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var campaign = await (await AccessibleAsync(orgId.Value))
            .Include(c => c.Segment)
            .Where(c => c.Id == id && c.OrgId == orgId.Value)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        if (campaign is null)
            return NotFound();

        return Ok(MapToCampaignDto(campaign));
    }

    /// <summary>
    /// Submits a campaign for approval (AC-NF24-5).
    /// </summary>
    [HttpPost("{id}/submit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitForApproval(Guid id)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        if (!await (await AccessibleAsync(orgId.Value)).AnyAsync(c => c.Id == id, HttpContext.RequestAborted)) return NotFound();
        await campaignService.SubmitForApprovalAsync(id, orgId.Value, HttpContext.RequestAborted);

        return NoContent();
    }

    /// <summary>
    /// Schedules an approved campaign (AC-NF24-6).
    /// </summary>
    [HttpPost("{id}/schedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ScheduleCampaign(Guid id, [FromBody] ScheduleCampaignRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        if (!await (await AccessibleAsync(orgId.Value)).AnyAsync(c => c.Id == id, HttpContext.RequestAborted)) return NotFound();
        await campaignService.ScheduleAsync(id, request.ScheduledAt, orgId.Value, HttpContext.RequestAborted);

        return NoContent();
    }

    #endregion

    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (!orgId.HasValue) return Unauthorized();
        var campaign = await (await AccessibleAsync(orgId.Value)).SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign == null) return NotFound();
        var metrics = await db.CampaignDeliveries.Where(d => d.OrgId == orgId.Value && d.CampaignId == id)
            .GroupBy(d => d.CampaignId).Select(g => new { Opened = g.Count(d => d.Opened), Clicked = g.Count(d => d.Clicked),
                Bounced = g.Count(d => d.Bounced), Unsubscribed = g.Count(d => d.UnsubscribedAt != null) }).SingleOrDefaultAsync(ct);
        return Ok(new { campaign.Id, campaign.Status, campaign.SentCount, campaign.ErrorCount, openedCount = metrics?.Opened ?? 0,
            clickedCount = metrics?.Clicked ?? 0, bouncedCount = metrics?.Bounced ?? 0,
            unsubscribeCount = metrics?.Unsubscribed ?? campaign.UnsubscribeCount });
    }

    private static CampaignDto MapToCampaignDto(Campaign campaign)
    {
        return new CampaignDto
        {
            Id = campaign.Id,
            PropertyId = campaign.PropertyId,
            SegmentId = campaign.SegmentId,
            SegmentName = campaign.Segment?.Name,
            Subject = campaign.Subject,
            Body = campaign.Body,
            IsAiGenerated = campaign.IsAiGenerated,
            Status = campaign.Status,
            ScheduledAt = campaign.ScheduledAt,
            SentAt = campaign.SentAt,
            SentCount = campaign.SentCount,
            ErrorCount = campaign.ErrorCount,
            UnsubscribeCount = campaign.UnsubscribeCount,
            CreatedAt = campaign.CreatedAt
        };
    }
}

#region DTOs

public record CreateSegmentRequest
{
    public required string Name { get; init; }
    public required Condition Condition { get; init; }
}

public record GuestSegmentDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public Condition? Condition { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record SegmentPreviewDto
{
    public int Count { get; init; }
}

public record CreateCampaignRequest
{
    public Guid? PropertyId { get; init; }
    public Guid SegmentId { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public bool IsAiGenerated { get; init; }
}

public record CampaignDto
{
    public Guid? PropertyId { get; init; }
    public Guid Id { get; init; }
    public Guid SegmentId { get; init; }
    public string? SegmentName { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public bool IsAiGenerated { get; init; }
    public CampaignStatus Status { get; init; }
    public DateTime? ScheduledAt { get; init; }
    public DateTime? SentAt { get; init; }
    public int SentCount { get; init; }
    public int ErrorCount { get; init; }
    public int UnsubscribeCount { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record ScheduleCampaignRequest
{
    public DateTime ScheduledAt { get; init; }
}

#endregion
