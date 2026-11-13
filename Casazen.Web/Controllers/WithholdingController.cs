using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// The withholding of the owners (ADR-018 amended, E06/F05/US02/T03): the evidence of the confirmed payouts of a month, the F24 data built from it, the
/// annual Certificazione Unica data, their filing states (generated is never filed: submitted only through a controlled manual handoff, then accepted
/// with the receipt or rejected) and the reconciliation of the obligations to the F24 and to the annual data. No rate or tax code is written here: they
/// come from the approved rule the evidence recorded. Every action needs <c>agency.withholding.manage</c> (DEB-01); the filing states also
/// <c>agency.owner.financial.manage</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.AgencyWithholdingManage)]
public class WithholdingController(
    AppDbContext db,
    IWithholdingService withholdingService,
    IOwnerPayoutService payouts,
    IStatementExportService statementExport,
    IOrgContextResolver orgContextResolver,
    ILogger<WithholdingController> logger) : ControllerBase
{
    /// <summary>The applicable withholding evidence of a month (the payouts paid in it and their reversals), per owner.</summary>
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

        var evidence = await db.TaxObligationEvidence
            .AsNoTracking()
            .Where(e => e.OrgId == orgId.Value && e.Decision == TaxDecision.Applicable && e.PeriodYear == year && e.PeriodMonth == month)
            .ToListAsync(HttpContext.RequestAborted);
        var ownerIds = evidence.Select(e => e.PropertyOwnerId).Distinct().ToList();
        var names = await db.PropertyOwners
            .AsNoTracking()
            .Where(o => o.OrgId == orgId.Value && ownerIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => $"{o.FirstName} {o.LastName}", HttpContext.RequestAborted);

        var ownerSummaries = evidence
            .GroupBy(e => e.PropertyOwnerId)
            .Select(g => new OwnerWithholdingSummaryDto
            {
                OwnerId = g.Key,
                OwnerName = names.GetValueOrDefault(g.Key, string.Empty),
                TotalBase = g.Sum(e => e.GrossBase ?? 0m),
                TotalWithholding = g.Sum(e => e.Amount ?? 0m),
                PayoutCount = g.Where(e => e.ReversesEvidenceId is null).Select(e => e.OwnerPayoutId).Distinct().Count(),
            })
            .OrderBy(s => s.OwnerName)
            .ToList();

        return Ok(new WithholdingSummaryDto
        {
            Month = month,
            Year = year,
            TotalBase = ownerSummaries.Sum(s => s.TotalBase),
            TotalWithholding = ownerSummaries.Sum(s => s.TotalWithholding),
            OwnerSummaries = ownerSummaries,
        });
    }

    /// <summary>
    /// The F24 record of a month; generated from the evidence when there is none yet. 422 <c>tax_no_obligations</c> (no applicable withholding in the
    /// month: a non-applicable or Swiss decision never makes an F24), <c>f24_tax_code_mixed</c>.
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
            .FirstOrDefaultAsync(HttpContext.RequestAborted)
            ?? await withholdingService.GenerateF24Async(orgId.Value, month, year, HttpContext.RequestAborted);
        return Ok(F24RecordDto.From(f24));
    }

    /// <summary>Builds the F24 of a month again from the evidence while it is only generated. 409 <c>tax_file_already_filed</c>.</summary>
    [HttpPost("f24/generate")]
    [ProducesResponseType(typeof(F24RecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<F24RecordDto>> RegenerateF24([FromQuery] int month, [FromQuery] int year)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();
        return Ok(F24RecordDto.From(await withholdingService.GenerateF24Async(orgId.Value, month, year, HttpContext.RequestAborted)));
    }

    /// <summary>
    /// The stored F24 data of the month as a PDF (NF-25, E06/F01/US01/T02): tax code, period, amount, due date, payment and filing state as the record
    /// holds them, with the label that it is the data to fill the form with. It never generates the record: a month without one is 404
    /// <c>f24_not_found</c>.
    /// </summary>
    /// <param name="month">Reference month (1-12).</param>
    /// <param name="year">Reference year (e.g., 2026).</param>
    [HttpGet("f24/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetF24Pdf([FromQuery] int month, [FromQuery] int year)
    {
        logger.LogInformation("Exporting the F24 of {Year}-{Month} as PDF", year, month);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var file = await statementExport.ExportF24Async(orgId.Value, month, year, DocumentLanguages.Current, HttpContext.RequestAborted);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// Moves an F24 along its filing states: <c>reviewed</c>, <c>submitted</c> (with the <c>handoffReference</c> of the controlled manual handoff),
    /// <c>accepted</c> (with the <c>receiptReference</c>) or <c>rejected</c> (with the <c>reason</c>). 409 <c>tax_filing_transition_invalid</c>; 422
    /// <c>tax_filing_reference_required</c>.
    /// </summary>
    [HttpPost("f24/{f24Id:guid}/filing-state")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(F24RecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<F24RecordDto>> SetF24FilingState(Guid f24Id, [FromBody] TaxFilingCommand command)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();
        var record = await payouts.SetF24FilingStateAsync(orgId.Value, f24Id, command, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(F24RecordDto.From(record));
    }

    /// <summary>Reconcile F24 payment: marks the F24 as paid (the payment, not its filing state).</summary>
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

    /// <summary>The annual Certificazione Unica data of the year (404 while there is none).</summary>
    [HttpGet("annual/{year:int}")]
    [ProducesResponseType(typeof(TaxReportingDatasetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaxReportingDatasetDto>> GetAnnual(int year)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();
        var dataset = await payouts.GetAnnualDatasetAsync(orgId.Value, year, HttpContext.RequestAborted);
        return dataset is null ? this.ApiProblem(StatusCodes.Status404NotFound, OwnerPayoutErrors.DatasetNotFound, "TaxDatasetNotFound") : Ok(TaxReportingDatasetDto.From(dataset));
    }

    /// <summary>Builds the annual data of the year from the evidence, again while it is only generated. 422 <c>tax_no_obligations</c>; 409 <c>tax_file_already_filed</c>.</summary>
    [HttpPost("annual/{year:int}")]
    [ProducesResponseType(typeof(TaxReportingDatasetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TaxReportingDatasetDto>> GenerateAnnual(int year)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();
        var dataset = await payouts.GenerateAnnualDatasetAsync(orgId.Value, year, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(TaxReportingDatasetDto.From(dataset));
    }

    /// <summary>Moves the annual data along its filing states, as the F24. 409 <c>tax_filing_transition_invalid</c>; 422 <c>tax_filing_reference_required</c>.</summary>
    [HttpPost("annual/datasets/{datasetId:guid}/filing-state")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(TaxReportingDatasetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TaxReportingDatasetDto>> SetAnnualFilingState(Guid datasetId, [FromBody] TaxFilingCommand command)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();
        var dataset = await payouts.SetDatasetFilingStateAsync(orgId.Value, datasetId, command, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(TaxReportingDatasetDto.From(dataset));
    }

    /// <summary>The obligations of each month of the year against their F24, and of the year against the annual data.</summary>
    [HttpGet("reconciliation/{year:int}")]
    [ProducesResponseType(typeof(TaxReconciliation), StatusCodes.Status200OK)]
    public async Task<ActionResult<TaxReconciliation>> Reconciliation(int year)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();
        return Ok(await payouts.ReconcileAsync(orgId.Value, year, HttpContext.RequestAborted));
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
    public int PayoutCount { get; init; }

    /// <summary>The earlier name of <see cref="PayoutCount"/>, kept for the clients that read it: the payouts, not the bookings, since E06/F05/US02/T03.</summary>
    public int BookingCount => PayoutCount;
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
    public TaxFilingState FilingState { get; init; }
    public string? RuleVersion { get; init; }
    public string? HandoffReference { get; init; }
    public string? ReceiptReference { get; init; }
    public string? RejectionReason { get; init; }

    public static F24RecordDto From(F24Record f24) => new()
    {
        Id = f24.Id,
        ReferenceMonth = f24.ReferenceMonth,
        ReferenceYear = f24.ReferenceYear,
        TotalAmount = f24.TotalAmount,
        TaxCode = f24.TaxCode,
        DueDate = f24.DueDate,
        PaidAt = f24.PaidAt,
        IsOverdue = f24.PaidAt is null && f24.DueDate < DateTime.UtcNow,
        CreatedAt = f24.CreatedAt,
        FilingState = f24.FilingState,
        RuleVersion = f24.RuleVersion,
        HandoffReference = f24.HandoffReference,
        ReceiptReference = f24.ReceiptReference,
        RejectionReason = f24.RejectionReason,
    };
}

/// <summary>The annual tax data with its filing state.</summary>
public sealed record TaxReportingDatasetDto(
    Guid Id,
    string Kind,
    int Year,
    decimal TotalGrossBase,
    decimal TotalWithheld,
    int EvidenceCount,
    string EvidenceSha256,
    string PayloadJson,
    TaxFilingState FilingState,
    string? HandoffReference,
    string? ReceiptReference,
    string? RejectionReason,
    DateTime GeneratedAt)
{
    public static TaxReportingDatasetDto From(TaxReportingDataset d) => new(
        d.Id, d.Kind, d.Year, d.TotalGrossBase, d.TotalWithheld, d.EvidenceCount, d.EvidenceSha256, d.PayloadJson, d.FilingState, d.HandoffReference,
        d.ReceiptReference, d.RejectionReason, d.GeneratedAt);
}

/// <summary>Request to reconcile F24 payment.</summary>
public record ReconcileF24Request
{
    public Guid F24Id { get; init; }
    public DateTime PaidAt { get; init; }
}
