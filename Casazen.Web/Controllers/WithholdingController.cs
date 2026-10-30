using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Withholding tax management (ADR-018). Calculates withholding tax on owner payments,
/// generates F24 forms, and tracks payment reconciliation.
/// Agency acting as sostituto d'imposta withholds 21% tax on short-term rental payments.
/// Regulatory references: D.L. 50/2017 art. 4 (current), D.Lgs. 117/2026 artt. 207-208 (expected 1/1/2027).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
public class WithholdingController(
    AppDbContext db,
    IWithholdingService withholdingService,
    IOrgContextResolver orgContextResolver,
    ILogger<WithholdingController> logger) : ControllerBase
{
    /// <summary>
    /// Withholding summary for a specific month and year.
    /// Returns aggregated withholding logs grouped by owner.
    /// </summary>
    /// <param name="month">Reference month (1-12).</param>
    /// <param name="year">Reference year (e.g., 2026).</param>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(WithholdingSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<WithholdingSummaryDto>> GetSummary(
        [FromQuery] int month,
        [FromQuery] int year)
    {
        logger.LogInformation("Retrieving withholding summary for {Year}-{Month}", year, month);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var logs = await db.WithholdingLogs
            .Include(w => w.Owner)
            .Include(w => w.Property)
            .Where(w => w.OrgId == orgId.Value && w.Month == month && w.Year == year)
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted);

        var ownerSummaries = logs
            .GroupBy(w => new { w.OwnerId, OwnerName = $"{w.Owner.FirstName} {w.Owner.LastName}" })
            .Select(g => new OwnerWithholdingSummaryDto
            {
                OwnerId = g.Key.OwnerId,
                OwnerName = g.Key.OwnerName,
                TotalBase = g.Sum(w => w.GrossBase),
                TotalWithholding = g.Sum(w => w.WithholdingAmount),
                BookingCount = g.Count()
            })
            .OrderBy(s => s.OwnerName)
            .ToList();

        var summary = new WithholdingSummaryDto
        {
            Month = month,
            Year = year,
            TotalBase = logs.Sum(w => w.GrossBase),
            TotalWithholding = logs.Sum(w => w.WithholdingAmount),
            OwnerSummaries = ownerSummaries
        };

        return Ok(summary);
    }

    /// <summary>
    /// F24 record for a specific month and year.
    /// Returns the F24 if it exists, or generates it if not.
    /// </summary>
    /// <param name="month">Reference month (1-12).</param>
    /// <param name="year">Reference year (e.g., 2026).</param>
    [HttpGet("f24")]
    [ProducesResponseType(typeof(F24RecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<F24RecordDto>> GetF24(
        [FromQuery] int month,
        [FromQuery] int year)
    {
        logger.LogInformation("Retrieving F24 for {Year}-{Month}", year, month);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var f24 = await db.F24Records
            .Where(f => f.OrgId == orgId.Value && f.ReferenceMonth == month && f.ReferenceYear == year)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        // If F24 doesn't exist, generate it
        if (f24 is null)
        {
            f24 = await withholdingService.GenerateF24Async(orgId.Value, month, year, HttpContext.RequestAborted);
        }

        var dto = new F24RecordDto
        {
            Id = f24.Id,
            ReferenceMonth = f24.ReferenceMonth,
            ReferenceYear = f24.ReferenceYear,
            TotalAmount = f24.TotalAmount,
            TaxCode = f24.TaxCode,
            DueDate = f24.DueDate,
            PaidAt = f24.PaidAt,
            IsOverdue = f24.PaidAt is null && f24.DueDate < DateTime.UtcNow,
            CreatedAt = f24.CreatedAt
        };

        return Ok(dto);
    }

    /// <summary>
    /// Reconcile F24 payment. Marks the F24 as paid.
    /// </summary>
    /// <param name="request">Reconciliation details.</param>
    [HttpPost("reconcile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Reconcile([FromBody] ReconcileF24Request request)
    {
        logger.LogInformation("Reconciling F24 {F24Id}", request.F24Id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var f24 = await db.F24Records
            .Where(f => f.Id == request.F24Id && f.OrgId == orgId.Value)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        if (f24 is null)
        {
            return this.ApiProblem(
                StatusCodes.Status404NotFound,
                "f24_not_found",
                "F24NotFound");
        }

        f24.PaidAt = request.PaidAt;
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok();
    }
}

/// <summary>DTO for withholding summary response.</summary>
public record WithholdingSummaryDto
{
    public int Month { get; init; }
    public int Year { get; init; }
    public decimal TotalBase { get; init; }
    public decimal TotalWithholding { get; init; }
    public List<OwnerWithholdingSummaryDto> OwnerSummaries { get; init; } = new();
}

/// <summary>DTO for per-owner withholding summary.</summary>
public record OwnerWithholdingSummaryDto
{
    public Guid OwnerId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public decimal TotalBase { get; init; }
    public decimal TotalWithholding { get; init; }
    public int BookingCount { get; init; }
}

/// <summary>DTO for F24 record.</summary>
public record F24RecordDto
{
    public Guid Id { get; init; }
    public int ReferenceMonth { get; init; }
    public int ReferenceYear { get; init; }
    public decimal TotalAmount { get; init; }
    public string TaxCode { get; init; } = string.Empty;
    public DateTime DueDate { get; init; }
    public DateTime? PaidAt { get; init; }
    public bool IsOverdue { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Request to reconcile F24 payment.</summary>
public record ReconcileF24Request
{
    public Guid F24Id { get; init; }
    public DateTime PaidAt { get; init; }
}
