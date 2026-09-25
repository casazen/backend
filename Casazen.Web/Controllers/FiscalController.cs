using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/fiscal")]
[Authorize(Policy = "RequireContext:short-rent:property.read")]
public class FiscalController(
    IFiscalRegimeService fiscalRegime,
    IFiscalReportingService fiscalReporting,
    IOrgContextResolver orgContextResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("regime")]
    public async Task<IActionResult> GetRegime([FromQuery] int taxYear, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();
        try
        {
            var snapshot = await fiscalRegime.GetRegimeAsync(orgId.Value, taxYear, cancellationToken);
            return Ok(snapshot);
        }
        catch (FiscalValidationException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Validation error", Detail = ex.Message, Status = 400 });
        }
    }

    [HttpPut("properties/{propertyId:guid}/regime")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<IActionResult> AssignRegime(Guid propertyId, [FromBody] AssignRegimeRequest request, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();
        try
        {
            var row = await fiscalRegime.AssignRegimeAsync(
                orgId.Value, propertyId, request.TaxYear, request.Regime, request.IsPrimaryForCedolare, cancellationToken);
            return Ok(row);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (FiscalValidationException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Validation error", Detail = ex.Message, Status = 400 });
        }
        // Over the short-rental threshold the service throws DomainConflictException (409, code
        // fiscal_short_stay_threshold_exceeded), turned into a localized problem by the error middleware.
    }

    /// <summary>
    /// Records the taxpayer (titolare fiscale) who lets the property, by codice fiscale, or clears it (<c>null</c>: the org
    /// tax profile). The short-rental threshold and the 21% cedolare unit are counted per taxpayer (CO-18, A5-22).
    /// </summary>
    [HttpPut("properties/{propertyId:guid}/taxpayer")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    public async Task<IActionResult> SetTaxpayer(Guid propertyId, [FromBody] SetPropertyTaxpayerRequest request, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        // A property of another org, or of another owner for a caller without org-wide access, answers 404.
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null || !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Write))
            return NotFound();

        try
        {
            return Ok(await fiscalRegime.SetPropertyTaxpayerAsync(orgId.Value, propertyId, request.FiscalCode, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("tax-profile")]
    public async Task<IActionResult> GetTaxProfile(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();
        try
        {
            return Ok(await fiscalRegime.GetTaxProfileAsync(orgId.Value, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("tax-profile")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<IActionResult> PutTaxProfile([FromBody] UpdateTaxProfileRequest request, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();
        try
        {
            return Ok(await fiscalRegime.UpdateTaxProfileAsync(
                orgId.Value, request.HasPartitaIva, request.PartitaIvaNumber, request.FiscalCode, cancellationToken));
        }
        catch (FiscalValidationException)
        {
            return BadRequest(new ProblemDetails { Title = "Validation error", Detail = "Invalid tax identifier.", Status = 400 });
        }
    }

    [HttpGet("reports/annual/{taxYear:int}")]
    public async Task<IActionResult> AnnualReport(int taxYear, [FromQuery] string format = "json", CancellationToken cancellationToken = default)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();
        try
        {
            var report = await fiscalReporting.GetAnnualReportAsync(orgId.Value, taxYear, cancellationToken);
            return Export(format, $"casazen-redditi-{taxYear}", report, () => fiscalReporting.ToCsv(report),
                () => fiscalReporting.ToPdf(report.PackLabel, $"{report.Disclaimer}\nGross {report.Totals.GrossIncome} withholding {report.Totals.Withholding} net {report.Totals.Net}"));
        }
        catch (FiscalValidationException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Validation error", Detail = ex.Message, Status = 400 });
        }
    }

    [HttpGet("reports/withholding/{taxYear:int}")]
    public async Task<IActionResult> WithholdingReport(int taxYear, [FromQuery] string format = "json", CancellationToken cancellationToken = default)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();
        try
        {
            var report = await fiscalReporting.GetWithholdingReportAsync(orgId.Value, taxYear, cancellationToken);
            return Export(format, $"casazen-ritenute-{taxYear}", report, () => fiscalReporting.ToCsv(report),
                () => fiscalReporting.ToPdf(report.PackLabel, string.Join('\n', report.ByOta.Select(b => $"{b.Source} {b.Withholding}"))));
        }
        catch (FiscalValidationException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Validation error", Detail = ex.Message, Status = 400 });
        }
    }

    [HttpPost("simulate")]
    public async Task<IActionResult> Simulate([FromBody] FiscalSimulateRequest request, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();
        try
        {
            return Ok(await fiscalRegime.SimulateAsync(orgId.Value, request.TaxYear, request.HypotheticalStrCount, cancellationToken));
        }
        catch (FiscalValidationException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Validation error", Detail = ex.Message, Status = 400 });
        }
    }

    private IActionResult Export<T>(string format, string fileBase, T json, Func<byte[]> csv, Func<byte[]> pdf)
    {
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return File(csv(), "text/csv", $"{fileBase}.csv");
        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
            return File(pdf(), "application/pdf", $"{fileBase}.pdf");
        return Ok(json);
    }
}

public record AssignRegimeRequest(int TaxYear, StrFiscalRegime Regime, bool? IsPrimaryForCedolare);
public record UpdateTaxProfileRequest(bool HasPartitaIva, string? PartitaIvaNumber, string? FiscalCode);
public record FiscalSimulateRequest(int TaxYear, int? HypotheticalStrCount);

/// <param name="FiscalCode">Codice fiscale of the taxpayer (16 characters; spaces ignored), or null to clear it.</param>
public record SetPropertyTaxpayerRequest([StringLength(32)] string? FiscalCode);
