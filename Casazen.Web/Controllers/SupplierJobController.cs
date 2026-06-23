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

    /// <summary>Creates a new job assigned to a supplier (used by hosts or admin).</summary>
    [HttpPost]
    public async Task<ActionResult<SupplierJobDto>> CreateJob(
        [FromBody] CreateSupplierJobRequest request,
        CancellationToken ct)
    {
        var orgId = await _orgResolver.GetOrProvisionSupplierOrgIdAsync(ct);
        if (orgId is null) return NotFound();

        var job = new SupplierJob
        {
            SupplierOrgId = orgId.Value,
            Description = request.Description,
            PropertyAddress = request.PropertyAddress,
            ScheduledStartUtc = request.ScheduledStartUtc,
            ScheduledEndUtc = request.ScheduledEndUtc,
            Price = request.Price,
            Status = SupplierJobStatus.Offered,
        };
        _db.SupplierJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetJobs), MapJob(job));
    }

    /// <summary>Supplier accepts an offered job — generates and persists the QR check-in token.</summary>
    [HttpPost("{jobId}/accept")]
    public async Task<ActionResult<SupplierJobDto>> AcceptJob(Guid jobId, CancellationToken ct)
    {
        var orgId = await _orgResolver.GetOrProvisionSupplierOrgIdAsync(ct);
        if (orgId is null) return NotFound();

        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.SupplierOrgId == orgId.Value, ct);
        if (job is null) return NotFound(new { error = "Job not found" });

        if (job.Status != SupplierJobStatus.Offered)
            return BadRequest(new { error = "Job is not in Offered status" });

        // Generate and persist the check-in token ONCE
        job.CheckInToken = QrCodeService.GenerateToken();
        job.CheckInTokenExpiresAt = DateTime.UtcNow.AddDays(1);
        job.Status = SupplierJobStatus.Accepted;
        job.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(MapJob(job));
    }

    [HttpPost("{jobId}/check-in")]
    public async Task<ActionResult> CheckIn(Guid jobId, [FromBody] CheckInRequest request, CancellationToken ct)
    {
        var orgId = await _orgResolver.GetOrProvisionSupplierOrgIdAsync(ct);
        if (orgId is null) return NotFound();

        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.SupplierOrgId == orgId.Value, ct);
        if (job is null) return NotFound(new { error = "Job not found" });

        var result = PerformCheckIn(job, request);
        if (result is OkObjectResult)
            await _db.SaveChangesAsync(ct);

        return result;
    }

    [HttpPost("{jobId}/check-out")]
    public async Task<ActionResult> CheckOut(Guid jobId, CancellationToken ct)
    {
        var orgId = await _orgResolver.GetOrProvisionSupplierOrgIdAsync(ct);
        if (orgId is null) return NotFound();

        var job = await _db.SupplierJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.SupplierOrgId == orgId.Value, ct);
        if (job is null) return NotFound();

        var result = PerformCheckOut(job);
        if (result is OkObjectResult)
            await _db.SaveChangesAsync(ct);

        return result;
    }

    /// <summary>Shared check-in logic used by both authenticated and public endpoints.</summary>
    internal static ActionResult PerformCheckIn(SupplierJob job, CheckInRequest request)
    {
        if (string.IsNullOrWhiteSpace(job.CheckInToken) || job.CheckInToken != request.Token)
            return new BadRequestObjectResult(new { error = "Invalid check-in token" });

        if (job.CheckInTokenExpiresAt < DateTime.UtcNow)
            return new BadRequestObjectResult(new { error = "Check-in token has expired" });

        if (job.Status != SupplierJobStatus.Accepted)
            return new BadRequestObjectResult(new { error = "Job is not in Accepted status" });

        var now = DateTime.UtcNow;
        if (now < job.ScheduledStartUtc.AddHours(-1))
            return new BadRequestObjectResult(new { error = "Too early for check-in. You can check in up to 1 hour before the scheduled time." });

        job.Status = SupplierJobStatus.InProgress;
        job.CheckedInAt = now;
        job.CheckInLocation = request.GpsLatitude.HasValue && request.GpsLongitude.HasValue
            ? $"{request.GpsLatitude},{request.GpsLongitude}"
            : null;
        job.UpdatedAt = now;

        return new OkObjectResult(new { status = "in_progress", checkedInAt = job.CheckedInAt });
    }

    /// <summary>Shared check-out logic used by both authenticated and public endpoints.</summary>
    internal static ActionResult PerformCheckOut(SupplierJob job)
    {
        if (job.Status != SupplierJobStatus.InProgress)
            return new BadRequestObjectResult(new { error = "Job is not in progress" });

        job.Status = SupplierJobStatus.Completed;
        job.CheckedOutAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        return new OkObjectResult(new { status = "completed", checkedOutAt = job.CheckedOutAt });
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
            ? _qrCodeService.BuildCheckInUrl(j.Id, j.CheckInToken, j.PropertyAddress)
            : string.Empty,
    };
}

/// <summary>
/// Public endpoint for check-in page — no auth required.
/// Uses a time-limited token for authorization.
/// Delegates to SupplierJobController shared logic to avoid duplication.
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
        var job = await _db.SupplierJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null || string.IsNullOrWhiteSpace(job.CheckInToken) || job.CheckInToken != token)
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
        var job = await _db.SupplierJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();

        var result = SupplierJobController.PerformCheckIn(job, request);
        if (result is OkObjectResult)
            await _db.SaveChangesAsync(ct);

        return result;
    }

    [HttpPost("{jobId}/check-out")]
    public async Task<ActionResult> PublicCheckOut(
        Guid jobId,
        [FromBody] CheckInRequest request,
        CancellationToken ct)
    {
        var job = await _db.SupplierJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || string.IsNullOrWhiteSpace(job.CheckInToken) || job.CheckInToken != request.Token)
            return NotFound();

        var result = SupplierJobController.PerformCheckOut(job);
        if (result is OkObjectResult)
            await _db.SaveChangesAsync(ct);

        return result;
    }
}