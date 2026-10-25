using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
/// </summary>
[ApiController]
[Route("api/campaigns")]
[Authorize(Policy = CasazenPolicies.GuestRead)]
public class CampaignsController(
    ICampaignService campaignService,
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    #region Segments

    /// <summary>
    /// Creates a guest segment (AC-NF24-1).
    /// </summary>
    [HttpPost("segments")]
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    [ProducesResponseType(typeof(GuestSegmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GuestSegmentDto>> CreateSegment([FromBody] CreateSegmentRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

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
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CampaignDto>> CreateCampaign([FromBody] CreateCampaignRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var campaign = await campaignService.CreateDraftAsync(
            orgId.Value,
            request.SegmentId,
            request.Subject,
            request.Body,
            request.IsAiGenerated,
            HttpContext.RequestAborted);

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

        var campaigns = await db.Campaigns
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

        var campaign = await db.Campaigns
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
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitForApproval(Guid id)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        await campaignService.SubmitForApprovalAsync(id, orgId.Value, HttpContext.RequestAborted);

        return NoContent();
    }

    /// <summary>
    /// Schedules an approved campaign (AC-NF24-6).
    /// </summary>
    [HttpPost("{id}/schedule")]
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ScheduleCampaign(Guid id, [FromBody] ScheduleCampaignRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        await campaignService.ScheduleAsync(id, request.ScheduledAt, orgId.Value, HttpContext.RequestAborted);

        return NoContent();
    }

    #endregion

    private static CampaignDto MapToCampaignDto(Campaign campaign)
    {
        return new CampaignDto
        {
            Id = campaign.Id,
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
    public Guid SegmentId { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public bool IsAiGenerated { get; init; }
}

public record CampaignDto
{
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
