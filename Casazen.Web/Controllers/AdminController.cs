using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Admin;
using Casazen.Web.DTOs.Orgs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class AdminController(
    IAdminService adminService,
    IOrgService orgService,
    IEntitlementService entitlementService,
    ILogger<AdminController> logger) : ControllerBase
{
    /// <summary>Returns platform KPI stats for the admin dashboard.</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<AdminStatsDto>> GetStats()
    {
        logger.LogInformation("Admin stats requested");

        var stats = await adminService.GetStatsAsync();
        return Ok(new AdminStatsDto
        {
            TotalProperties = stats.TotalProperties,
            ActiveProperties = stats.ActiveProperties,
            TotalBookings = stats.TotalBookings,
            BookingsThisMonth = stats.BookingsThisMonth,
            UpcomingCheckIns = stats.UpcomingCheckIns,
            TotalRevenue = stats.TotalRevenue,
            CinCompliance = new CinComplianceStats
            {
                Valid = stats.CinValid,
                Missing = stats.CinMissing,
                Invalid = stats.CinInvalid,
                Total = stats.CinTotal
            },
            OtaSyncHealth = new OtaSyncHealth
            {
                Synced = stats.OtaSynced,
                Failed = stats.OtaFailed,
                NeverSynced = stats.OtaNeverSynced
            }
        });
    }

    /// <summary>Returns a paginated CIN compliance report. Admin only.</summary>
    [HttpGet("cin-compliance")]
    public async Task<ActionResult<PagedResultDto<CinComplianceItemDto>>> GetCinCompliance(
        [FromQuery] string? cinStatus = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!string.IsNullOrWhiteSpace(cinStatus) &&
            cinStatus != "valid" && cinStatus != "missing" && cinStatus != "invalid")
        {
            return BadRequest(new { error = $"Unknown cinStatus value '{cinStatus}'" });
        }

        pageSize = Math.Min(pageSize, 100);

        try
        {
            var (items, total) = await adminService.GetCinComplianceAsync(cinStatus, page, pageSize);
            return Ok(new PagedResultDto<CinComplianceItemDto>
            {
                Items = items.Select(i => new CinComplianceItemDto
                {
                    PropertyId = i.PropertyId,
                    PropertyName = i.PropertyName,
                    OwnerId = i.OwnerId,
                    OwnerEmail = i.OwnerEmail,
                    CinCode = i.CinCode,
                    CinStatus = i.CinStatus,
                    City = i.City
                }),
                TotalCount = total,
                Page = page,
                PageSize = pageSize
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Returns the status of all registered Hangfire recurring jobs. Admin only.</summary>
    [HttpGet("jobs")]
    public async Task<ActionResult<IEnumerable<JobStatusDto>>> GetJobs()
    {
        var jobs = await adminService.GetJobStatusesAsync();
        return Ok(jobs.Select(j => new JobStatusDto
        {
            JobName = j.JobName,
            CronExpression = j.CronExpression,
            LastRun = j.LastRun,
            LastStatus = j.LastStatus,
            NextRun = j.NextRun
        }));
    }

    /// <summary>Updates an org's plan tier. Admin only (MVP until Stripe billing).</summary>
    [HttpPatch("orgs/{orgId:guid}/plan")]
    public async Task<ActionResult<EntitlementDto>> UpdateOrgPlan(
        Guid orgId,
        [FromBody] UpdateOrgPlanDto dto,
        CancellationToken cancellationToken)
    {
        if (!PlanCatalog.TryParseTier(dto.PlanTier, out var planTier))
            return BadRequest(new { error = $"Unknown planTier: {dto.PlanTier}" });

        logger.LogInformation("Admin plan change requested for org {OrgId} -> {PlanTier}", orgId, planTier);

        var updated = await orgService.UpdatePlanTierAsync(orgId, planTier, cancellationToken);
        if (updated is null)
            return NotFound(new { error = "Organization not found" });

        var entitlement = await entitlementService.GetEntitlementAsync(orgId, cancellationToken);
        return Ok(new EntitlementDto
        {
            OrgId = entitlement.OrgId,
            PlanTier = entitlement.PlanTier,
            Limits = new EntitlementLimitsDto { MaxProperties = entitlement.MaxProperties },
            Usage = new EntitlementUsageDto { Properties = entitlement.PropertyCount },
            CanAddProperty = entitlement.CanAddProperty
        });
    }
}
