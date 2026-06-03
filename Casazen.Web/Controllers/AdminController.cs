using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class AdminController(
    IAdminService adminService,
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
}
