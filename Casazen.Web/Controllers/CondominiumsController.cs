using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The owner's condominium year (CondoLedger): preventivo, consuntivo, rates, prior-year balance and the paid /
/// still-to-pay report. Reads need long-rent <c>property.read</c>; changes need <c>property.write</c>. Rows stay inside
/// the caller's org. Until a property is linked, only a caller who reaches the whole org can see or change it.
/// After that, the caller must reach the linked property.
/// </summary>
[ApiController]
[Route("api/long-rent/condominiums")]
[Authorize(Policy = CasazenPolicies.LongRentPropertyRead)]
public class CondominiumsController(
    ICondominiumAccountingService condominiums,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumView>> Create([FromBody] CreateCondominiumDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await condominiums.CreateCondominiumAsync(
            scope,
            new CreateCondominiumCommand(dto.Name, dto.AdministratorName, dto.AdministratorEmail, dto.AdministratorPhone, dto.FiscalStartMonth),
            userId,
            cancellationToken));
    }

    [HttpPost("{condominiumId:guid}/units")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumUnitView>> LinkProperty(
        Guid condominiumId, [FromBody] LinkPropertyDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await condominiums.LinkPropertyAsync(
            scope,
            condominiumId,
            new LinkPropertyCommand(dto.PropertyId, dto.UnitReference, dto.Millesimi, dto.ValidFrom, dto.ValidTo),
            userId,
            cancellationToken));
    }

    [HttpPost("{condominiumId:guid}/periods")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumPeriodView>> CreatePeriod(
        Guid condominiumId, [FromBody] CreateCondominiumPeriodDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await condominiums.CreatePeriodAsync(scope, condominiumId, new CreateCondominiumPeriodCommand(dto.Label), userId, cancellationToken));
    }

    [HttpPost("{condominiumId:guid}/periods/{periodId:guid}/budget")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<BudgetConfirmation>> ConfirmBudget(
        Guid condominiumId, Guid periodId, [FromBody] ConfirmBudgetDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await condominiums.ConfirmBudgetAsync(
            scope,
            condominiumId,
            periodId,
            new ConfirmBudgetCommand(
                dto.OrdinaryAmount,
                dto.Description,
                dto.Extraordinary?.Select(line => new ExtraordinaryExpenseCommand(line.Amount, line.Description)).ToList(),
                dto.Cadence),
            userId,
            cancellationToken));
    }

    [HttpPost("{condominiumId:guid}/periods/{periodId:guid}/final")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<FinalConfirmation>> ConfirmFinal(
        Guid condominiumId, Guid periodId, [FromBody] ConfirmFinalDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await condominiums.ConfirmFinalAsync(
            scope, condominiumId, periodId, new ConfirmFinalCommand(dto.Amount, dto.Settlement), userId, cancellationToken));
    }

    [HttpPost("{condominiumId:guid}/periods/{periodId:guid}/prior-balance")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<PriorYearBalanceView>> RecordPriorBalance(
        Guid condominiumId, Guid periodId, [FromBody] RecordPriorBalanceDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await condominiums.RecordPriorYearBalanceAsync(
            scope,
            condominiumId,
            periodId,
            new RecordPriorYearBalanceCommand(dto.Amount, dto.Disposition, dto.SourcePeriodId, dto.Description),
            userId,
            cancellationToken));
    }

    [HttpPost("{condominiumId:guid}/periods/{periodId:guid}/payments")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumPaymentView>> RecordPayment(
        Guid condominiumId, Guid periodId, [FromBody] RecordCondominiumPaymentDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await condominiums.RecordPaymentAsync(
            scope,
            condominiumId,
            periodId,
            new RecordCondominiumPaymentCommand(
                dto.InstallmentId, dto.CoversPriorBalance, dto.Amount, dto.PaidOn, dto.Method, dto.BankMovementId, dto.ExternalSourceHash, dto.Note),
            userId,
            cancellationToken));
    }

    /// <summary>The same year the PDFs print: budget, extraordinary, prior balance, final, paid and still to pay.</summary>
    [HttpGet("{condominiumId:guid}/periods/{periodId:guid}/report")]
    public async Task<ActionResult<SituazioneReport.Year>> Report(Guid condominiumId, Guid periodId, CancellationToken cancellationToken)
    {
        var (scope, _) = await ScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();
        return Ok(await condominiums.GetReportAsync(scope, condominiumId, periodId, cancellationToken));
    }

    [HttpGet("{condominiumId:guid}/periods/{periodId:guid}/report/paid.pdf")]
    public async Task<IActionResult> PaidPdf(Guid condominiumId, Guid periodId, CancellationToken cancellationToken)
    {
        var (scope, _) = await ScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();
        var pdf = await condominiums.RenderPaidPdfAsync(scope, condominiumId, periodId, cancellationToken);
        return File(pdf, "application/pdf", "spese-gia-pagate.pdf");
    }

    [HttpGet("{condominiumId:guid}/periods/{periodId:guid}/report/unpaid.pdf")]
    public async Task<IActionResult> UnpaidPdf(Guid condominiumId, Guid periodId, CancellationToken cancellationToken)
    {
        var (scope, _) = await ScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();
        var pdf = await condominiums.RenderUnpaidPdfAsync(scope, condominiumId, periodId, cancellationToken);
        return File(pdf, "application/pdf", "spese-ancora-da-pagare.pdf");
    }

    /// <summary>
    /// Posts a <c>condo-statement-v1</c> text or PDF into the fiscal year named in the file. The same numbers can be entered
    /// by hand. A free-form administrator PDF is refused: it is not read by a model.
    /// </summary>
    [HttpPost("{condominiumId:guid}/statements/import")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    [RequestSizeLimit(1_048_576)]
    public async Task<ActionResult<StatementImportResult>> Import(Guid condominiumId, IFormFile file, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        if (file is null || file.Length == 0)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, CondominiumErrorCodes.FileUnsupported, "CondominiumStatementFileUnsupported");

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return Ok(await condominiums.ImportStatementFileAsync(
            scope, condominiumId, new StatementFileUpload(file.FileName, buffer.ToArray()), userId, cancellationToken));
    }

    private async Task<(HostScope? Scope, string? UserId)> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return (null, null);
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        return (scope, User.GetUserId());
    }
}

public sealed class CreateCondominiumDto
{
    public string Name { get; set; } = string.Empty;

    public string? AdministratorName { get; set; }

    public string? AdministratorEmail { get; set; }

    public string? AdministratorPhone { get; set; }

    public int FiscalStartMonth { get; set; } = 6;
}

public sealed class LinkPropertyDto
{
    public Guid PropertyId { get; set; }

    public string? UnitReference { get; set; }

    public decimal? Millesimi { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }
}

public sealed class CreateCondominiumPeriodDto
{
    public string Label { get; set; } = string.Empty;
}

public sealed class ExtraordinaryExpenseDto
{
    public decimal Amount { get; set; }

    public string? Description { get; set; }
}

public sealed class ConfirmBudgetDto
{
    public decimal OrdinaryAmount { get; set; }

    public string? Description { get; set; }

    public List<ExtraordinaryExpenseDto>? Extraordinary { get; set; }

    public CondominiumInstallmentCadence Cadence { get; set; } = CondominiumInstallmentCadence.Monthly;
}

public sealed class ConfirmFinalDto
{
    public decimal Amount { get; set; }

    public PriorYearBalanceDisposition Settlement { get; set; }
}

public sealed class RecordPriorBalanceDto
{
    public decimal Amount { get; set; }

    public PriorYearBalanceDisposition Disposition { get; set; }

    public Guid? SourcePeriodId { get; set; }

    public string? Description { get; set; }
}

public sealed class RecordCondominiumPaymentDto
{
    public Guid? InstallmentId { get; set; }

    public bool CoversPriorBalance { get; set; }

    public decimal Amount { get; set; }

    public DateOnly PaidOn { get; set; }

    public string? Method { get; set; }

    public Guid? BankMovementId { get; set; }

    public string? ExternalSourceHash { get; set; }

    public string? Note { get; set; }
}
