using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Monthly owner statements (ADR-017). Statements are generated per property owner per property per month,
/// with revenue breakdown, commission, expenses, and net payable computed with banker's rounding.
/// Co-owners each receive their own statement, split by percentage (ADR-017-R3/R4).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
public class StatementsController(
    AppDbContext db,
    IStatementService statementService,
    IOrgContextResolver orgContextResolver,
    ILogger<StatementsController> logger) : ControllerBase
{
    // Error codes for statement operations (snake_case, stable)
    private const string PdfExportNotImplemented = "statement_pdf_export_not_implemented";
    private const string CsvExportNotImplemented = "statement_csv_export_not_implemented";

    /// <summary>
    /// List of owner statements for the caller's agency org. Ordered by year/month descending.
    /// Optionally filtered by property owner and/or property.
    /// </summary>
    /// <param name="propertyOwnerId">Optional: filter by property owner.</param>
    /// <param name="propertyId">Optional: filter by property.</param>
    [HttpGet]
    [ProducesResponseType(typeof(List<OwnerStatementDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<OwnerStatementDto>>> GetAll(
        [FromQuery] Guid? propertyOwnerId = null,
        [FromQuery] Guid? propertyId = null)
    {
        logger.LogInformation("Retrieving owner statements list");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var query = db.OwnerStatements
            .Include(s => s.PropertyOwner)
            .Include(s => s.Property)
            .Where(s => s.OrgId == orgId.Value);

        if (propertyOwnerId.HasValue)
            query = query.Where(s => s.PropertyOwnerId == propertyOwnerId.Value);

        if (propertyId.HasValue)
            query = query.Where(s => s.PropertyId == propertyId.Value);

        var statements = await query
            .OrderByDescending(s => s.Year)
            .ThenByDescending(s => s.Month)
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted);

        return Ok(statements.Select(s => new OwnerStatementDto
        {
            Id = s.Id,
            PropertyOwnerId = s.PropertyOwnerId,
            OwnerName = $"{s.PropertyOwner.FirstName} {s.PropertyOwner.LastName}",
            PropertyId = s.PropertyId,
            PropertyName = s.Property.Name,
            Month = s.Month,
            Year = s.Year,
            Currency = s.Currency,
            Status = s.Status.ToString(),
            TotalRevenue = s.TotalRevenue,
            Commission = s.Commission,
            Expenses = s.Expenses,
            WithholdingTax = s.WithholdingTax,
            NetPayable = s.NetPayable,
            OwnershipPercent = s.OwnershipPercent,
            PublishedAt = s.PublishedAt,
            CreatedAt = s.CreatedAt
        }).ToList());
    }

    /// <summary>
    /// Generates a new monthly statement for the given owner, property, and period.
    /// The statement is created in Draft status with line items for all bookings collected in that month.
    /// </summary>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(OwnerStatementDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerStatementDto>> Generate([FromBody] GenerateStatementRequest request)
    {
        logger.LogInformation(
            "Generating statement for owner {OwnerId}, property {PropertyId}, period {Year}-{Month}",
            request.PropertyOwnerId, request.PropertyId, request.Year, request.Month);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var statement = await statementService.GenerateStatementAsync(
            request.PropertyOwnerId,
            request.PropertyId,
            request.Month,
            request.Year,
            HttpContext.RequestAborted);

        // Reload with navigation properties for DTO
        statement = await db.OwnerStatements
            .Include(s => s.PropertyOwner)
            .Include(s => s.Property)
            .FirstAsync(s => s.Id == statement.Id, HttpContext.RequestAborted);

        var dto = new OwnerStatementDto
        {
            Id = statement.Id,
            PropertyOwnerId = statement.PropertyOwnerId,
            OwnerName = $"{statement.PropertyOwner.FirstName} {statement.PropertyOwner.LastName}",
            PropertyId = statement.PropertyId,
            PropertyName = statement.Property.Name,
            Month = statement.Month,
            Year = statement.Year,
            Currency = statement.Currency,
            Status = statement.Status.ToString(),
            TotalRevenue = statement.TotalRevenue,
            Commission = statement.Commission,
            Expenses = statement.Expenses,
            WithholdingTax = statement.WithholdingTax,
            NetPayable = statement.NetPayable,
            OwnershipPercent = statement.OwnershipPercent,
            PublishedAt = statement.PublishedAt,
            CreatedAt = statement.CreatedAt
        };

        return CreatedAtAction(nameof(GetAll), new { }, dto);
    }

    /// <summary>
    /// Approves a Draft statement, locking it for review. The statement can no longer be edited.
    /// An Approved statement must be reviewed by the host or agency before publishing.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Approve(Guid id)
    {
        logger.LogInformation("Approving statement {StatementId}", id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        await statementService.ApproveStatementAsync(id, HttpContext.RequestAborted);
        return Ok();
    }

    /// <summary>
    /// Publishes an Approved statement, sending it to the owner. The statement becomes immutable.
    /// Any corrections after publication must be made via adjustment rows in the next month's statement (ADR-017-R5).
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Publish(Guid id)
    {
        logger.LogInformation("Publishing statement {StatementId}", id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        await statementService.PublishStatementAsync(id, HttpContext.RequestAborted);
        return Ok();
    }

    /// <summary>
    /// Exports a statement as PDF (ADR-017-R7). Shows co-owner split with rounding disclosure if applicable.
    /// </summary>
    [HttpGet("{id:guid}/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult> GetPdf(Guid id)
    {
        logger.LogInformation("Exporting statement {StatementId} as PDF", id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        // TODO: Implement PDF generation (e.g., with QuestPDF)
        // For now, return 501 Not Implemented
        await Task.CompletedTask;
        return this.ApiProblem(
            StatusCodes.Status501NotImplemented,
            PdfExportNotImplemented,
            "StatementPdfExportNotImplemented");
    }

    /// <summary>
    /// Exports a statement as CSV (ADR-017). Line-by-line breakdown with all columns.
    /// </summary>
    [HttpGet("{id:guid}/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult> GetCsv(Guid id)
    {
        logger.LogInformation("Exporting statement {StatementId} as CSV", id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        // TODO: Implement CSV generation
        // For now, return 501 Not Implemented
        await Task.CompletedTask;
        return this.ApiProblem(
            StatusCodes.Status501NotImplemented,
            CsvExportNotImplemented,
            "StatementCsvExportNotImplemented");
    }
}

/// <summary>DTO for owner statement summary in list and detail responses.</summary>
public record OwnerStatementDto
{
    public Guid Id { get; init; }
    public Guid PropertyOwnerId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public Guid PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int Month { get; init; }
    public int Year { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotalRevenue { get; init; }
    public decimal Commission { get; init; }
    public decimal Expenses { get; init; }
    public decimal WithholdingTax { get; init; }
    public decimal NetPayable { get; init; }
    public decimal OwnershipPercent { get; init; }
    public DateTime? PublishedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Request to generate a new statement.</summary>
public record GenerateStatementRequest
{
    public Guid PropertyOwnerId { get; init; }
    public Guid PropertyId { get; init; }
    public int Month { get; init; }
    public int Year { get; init; }
}
