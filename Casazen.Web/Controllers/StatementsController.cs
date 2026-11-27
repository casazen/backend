using Casazen.Core.Documents;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
public class StatementsController(
    AppDbContext db,
    IStatementService statementService,
    IStatementExportService statementExport,
    IOrgContextResolver orgContextResolver,
    ILogger<StatementsController> logger) : ControllerBase
{
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

    [HttpPost("generate")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
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

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
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

    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
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

    [HttpGet("{id:guid}/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult> GetPdf(Guid id) => ExportAsync(id, StatementExportFormat.Pdf);

    [HttpGet("{id:guid}/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult> GetCsv(Guid id) => ExportAsync(id, StatementExportFormat.Csv);

    private async Task<ActionResult> ExportAsync(Guid id, StatementExportFormat format)
    {
        logger.LogInformation("Exporting statement {StatementId} as {Format}", id, format);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var file = await statementExport.ExportStatementAsync(id, orgId.Value, format, DocumentLanguages.Current, HttpContext.RequestAborted);
        return File(file.Content, file.ContentType, file.FileName);
    }
}

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

public record GenerateStatementRequest
{
    public Guid PropertyOwnerId { get; init; }
    public Guid PropertyId { get; init; }
    public int Month { get; init; }
    public int Year { get; init; }
}
