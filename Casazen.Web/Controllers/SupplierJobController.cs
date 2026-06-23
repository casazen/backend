using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/supplier/jobs")]
[Authorize(Policy = "RequireSupplier")]
public class SupplierJobController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly QrCodeService _qrCodeService;
    private readonly ISupplierOrgContextResolver _orgResolver;

    public SupplierJobController(
        AppDbContext db,
        QrCodeService qrCodeService,
        ISupplierOrgContextResolver orgResolver)
    {
        _db = db;
        _qrCodeService = qrCodeService;
        _orgResolver = orgResolver;
    }

    [HttpGet]
    public async Task<ActionResult<List<SupplierJobDto>>> GetJobs(CancellationToken ct)
    {
        var orgId = await _orgResolver.GetOrProvisionSupplierOrgIdAsync(ct);
        if (orgId is null) return NotFound();

        var jobs = await _db.SupplierJobs
            .Where(j => j.SupplierOrgId == orgId.Value)
            .OrderByDescending(j => j.ScheduledStartUtc)
            .Take(50)
            .Select(j => MapJob(j))
            .ToListAsync(ct);

        return Ok(jobs);
    }

    [HttpPost("{jobId}/check-in")]
    public async Task<ActionResult> CheckIn(Guid jobId, [FromBody] CheckInRequest request, CancellationToken ct)
    {
        var orgId = await _orgResolver.GetOrProvisionSupplierOrgIdAsync(ct);
        if (orgId is null) return NotFound();

        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.SupplierOrgId == orgId.Value, ct);
        if (job is null) return NotFound(new { error = "Job not found" });

        if (job.CheckInToken != request.Token || job.CheckInTokenExpiresAt < DateTime.UtcNow)
            return BadRequest(new { error = "Invalid or expired check-in token" });

        if (job.Status != SupplierJobStatus.Accepted)
            return BadRequest(new { error = "Job is not in Accepted status" });

        var now = DateTime.UtcNow;
        if (now < job.ScheduledStartUtc.AddHours(-1))
            return BadRequest(new { error = "Too early for check-in. You can check in up to 1 hour before the scheduled time." });

        job.Status = SupplierJobStatus.InProgress;
        job.CheckedInAt = now;
        job.CheckInLocation = request.GpsLatitude.HasValue && request.GpsLongitude.HasValue
            ? $"{request.GpsLatitude},{request.GpsLongitude}"
            : null;
        job.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        return Ok(new { status = "in_progress", checkedInAt = job.CheckedInAt });
    }

    [HttpPost("{jobId}/check-out")]
    public async Task<ActionResult> CheckOut(Guid jobId, CancellationToken ct)
    {
        var orgId = await _orgResolver.GetOrProvisionSupplierOrgIdAsync(ct);
        if (orgId is null) return NotFound();

        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.SupplierOrgId == orgId.Value, ct);
        if (job is null) return NotFound();

        if (job.Status != SupplierJobStatus.InProgress)
            return BadRequest(new { error = "Job is not in progress" });

        job.Status = SupplierJobStatus.Completed;
        job.CheckedOutAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new { status = "completed", checkedOutAt = job.CheckedOutAt });
    }

    private SupplierJobDto MapJob(SupplierJob j) => new()
    {
        Id = j.Id,
        Status = j.Status.ToString(),
        Description = j.Description,
        PropertyAddress = j.PropertyAddress,
        ScheduledStartUtc = j.ScheduledStartUtc,
        ScheduledEndUtc = j.ScheduledEndUtc,
        Price = j.Price,
        CheckedInAt = j.CheckedInAt,
        CheckedOutAt = j.CheckedOutAt,
        CheckInUrl = j.CheckInToken is not null
            ? _qrCodeService.GenerateCheckInUrl(j.Id, j.PropertyAddress ?? "Property")
            : string.Empty,
    };
}

/// <summary>
/// Public endpoint for check-in page — no auth required.
/// Uses a time-limited token for authorization.
/// </summary>
[ApiController]
[Route("api/public/check-in")]
[AllowAnonymous]
public class PublicCheckInController : ControllerBase
{
    private readonly AppDbContext _db;

    public PublicCheckInController(AppDbContext db) => _db = db;

    [HttpGet("{jobId}")]
    public async Task<ActionResult<CheckInStatusDto>> GetCheckInStatus(
        Guid jobId,
        [FromQuery] string token,
        CancellationToken ct)
    {
        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null || job.CheckInToken != token)
            return NotFound(new { error = "Invalid or expired check-in link" });

        if (job.CheckInTokenExpiresAt < DateTime.UtcNow)
            return BadRequest(new { error = "Check-in token has expired" });

        return Ok(new CheckInStatusDto
        {
            JobId = job.Id,
            PropertyAddress = job.PropertyAddress ?? "N/A",
            Description = job.Description,
            Status = job.Status.ToString(),
            ScheduledStartUtc = job.ScheduledStartUtc,
            ScheduledEndUtc = job.ScheduledEndUtc,
            CanCheckIn = job.Status == SupplierJobStatus.Accepted,
            CanCheckOut = job.Status == SupplierJobStatus.InProgress,
            CheckedInAt = job.CheckedInAt,
            CheckedOutAt = job.CheckedOutAt,
        });
    }

    [HttpPost("{jobId}/check-in")]
    public async Task<ActionResult> PublicCheckIn(
        Guid jobId,
        [FromBody] CheckInRequest request,
        CancellationToken ct)
    {
        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null || job.CheckInToken != request.Token)
            return NotFound(new { error = "Invalid or expired check-in link" });

        if (job.CheckInTokenExpiresAt < DateTime.UtcNow)
            return BadRequest(new { error = "Check-in token has expired" });

        if (job.Status != SupplierJobStatus.Accepted)
            return BadRequest(new { error = "Job is not in Accepted status" });

        var now = DateTime.UtcNow;
        if (now < job.ScheduledStartUtc.AddHours(-1))
            return BadRequest(new { error = "Too early for check-in" });

        job.Status = SupplierJobStatus.InProgress;
        job.CheckedInAt = now;
        job.CheckInLocation = request.GpsLatitude.HasValue && request.GpsLongitude.HasValue
            ? $"{request.GpsLatitude},{request.GpsLongitude}"
            : null;
        job.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        return Ok(new { status = "in_progress", checkedInAt = job.CheckedInAt });
    }

    [HttpPost("{jobId}/check-out")]
    public async Task<ActionResult> PublicCheckOut(
        Guid jobId,
        [FromBody] CheckInRequest request,
        CancellationToken ct)
    {
        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null || job.CheckInToken != request.Token)
            return NotFound();

        if (job.Status != SupplierJobStatus.InProgress)
            return BadRequest(new { error = "Job is not in progress" });

        job.Status = SupplierJobStatus.Completed;
        job.CheckedOutAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new { status = "completed", checkedOutAt = job.CheckedOutAt });
    }
}
