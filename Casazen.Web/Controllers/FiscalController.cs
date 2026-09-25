using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
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
            return FiscalProblem(ex);
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
            return FiscalProblem(ex);
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

    /// <summary>
    /// Updates only the fields sent (CO-19, A5-23): a missing or null field keeps its saved value; <c>fiscalCode: ""</c>
    /// clears the codice fiscale.
    /// </summary>
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
                orgId.Value,
                new FiscalTaxProfileUpdate(request.HasPartitaIva, request.PartitaIvaNumber, request.FiscalCode),
                cancellationToken));
        }
        catch (FiscalValidationException ex)
        {
            return FiscalProblem(ex);
        }
    }

    /// <summary>
    /// Fiscal summary per property and taxpayer (CO-19): collected gross, tourist tax, gross rent, withholding and the tax
    /// estimated for the cedolare secca only. Period: <paramref name="from"/>-<paramref name="to"/> (calendar dates, both
    /// included) inside <paramref name="taxYear"/>, the whole year by default. <c>format</c>: json, csv or pdf.
    /// </summary>
    [HttpGet("reports/annual/{taxYear:int}")]
    public async Task<IActionResult> AnnualReport(
        int taxYear,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string format = "json",
        CancellationToken cancellationToken = default)
    {
        var scope = await GetScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();
        try
        {
            var report = await fiscalReporting.GetAnnualReportAsync(scope, taxYear, YearPeriod(taxYear, from, to), cancellationToken);
            return Export(format, FileBase("casazen-redditi", report.Period), report,
                () => fiscalReporting.ToCsv(report), () => fiscalReporting.ToPdf(report));
        }
        catch (FiscalValidationException ex)
        {
            return FiscalProblem(ex);
        }
    }

    /// <summary>OTA withholding per intermediary and per payment (CO-19). Same period rules as the summary.</summary>
    [HttpGet("reports/withholding/{taxYear:int}")]
    public async Task<IActionResult> WithholdingReport(
        int taxYear,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string format = "json",
        CancellationToken cancellationToken = default)
    {
        var scope = await GetScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();
        try
        {
            var report = await fiscalReporting.GetWithholdingReportAsync(scope, taxYear, YearPeriod(taxYear, from, to), cancellationToken);
            return Export(format, FileBase("casazen-ritenute", report.Period), report,
                () => fiscalReporting.ToCsv(report), () => fiscalReporting.ToPdf(report));
        }
        catch (FiscalValidationException ex)
        {
            return FiscalProblem(ex);
        }
    }

    /// <summary>
    /// Tourist tax per comune and month (CO-19, A5-16): stays with check-in between <paramref name="from"/> and
    /// <paramref name="to"/> (both required, at most one year) and the amounts recorded by the tourist tax engine (BK-03).
    /// </summary>
    [HttpGet("reports/tourist-tax")]
    public async Task<IActionResult> TouristTaxReport(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string format = "json",
        CancellationToken cancellationToken = default)
    {
        var scope = await GetScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();
        if (from is not DateOnly start || to is not DateOnly end)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "fiscal_report_period_invalid", "FiscalReportPeriodInvalid");
        try
        {
            var report = await fiscalReporting.GetTouristTaxReportAsync(scope, new FiscalReportPeriod(start, end), cancellationToken);
            return Export(format, FileBase("casazen-tassa-soggiorno", report.Period), report,
                () => fiscalReporting.ToCsv(report), () => fiscalReporting.ToPdf(report));
        }
        catch (FiscalValidationException ex)
        {
            return FiscalProblem(ex);
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
            return FiscalProblem(ex);
        }
    }

    private IActionResult FiscalProblem(FiscalValidationException ex) =>
        this.ApiProblem(StatusCodes.Status400BadRequest, ex.Code, ex.MessageKey, ex.MessageArgs);

    /// <summary>
    /// The caller's reach for the reports (TN-3): the whole org for org-wide roles, only the properties the caller owns
    /// otherwise; null when unauthenticated.
    /// </summary>
    private async Task<HostScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : User.GetHostScope(orgId.Value);
    }

    /// <summary>Period inside the tax year: null (the whole year) when neither bound is given, else the missing bound is the year's.</summary>
    private static FiscalReportPeriod? YearPeriod(int taxYear, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
            return null;
        var year = FiscalReportPeriod.WholeYear(Math.Clamp(taxYear, DateOnly.MinValue.Year, DateOnly.MaxValue.Year - 1));
        return new FiscalReportPeriod(from ?? year.From, to ?? year.To);
    }

    private static string FileBase(string prefix, FiscalReportPeriod period) =>
        $"{prefix}-{period.From:yyyyMMdd}-{period.To:yyyyMMdd}";

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
/// <summary>Partial update of the tax profile: a missing or null field keeps its saved value (CO-19).</summary>
/// <param name="HasPartitaIva">false also clears the saved partita IVA number.</param>
/// <param name="PartitaIvaNumber">11 digits; only with a partita IVA (sent now or already saved).</param>
/// <param name="FiscalCode">Codice fiscale; an empty string clears it.</param>
public record UpdateTaxProfileRequest(
    bool? HasPartitaIva,
    [StringLength(32)] string? PartitaIvaNumber,
    [StringLength(32)] string? FiscalCode);
public record FiscalSimulateRequest(int TaxYear, int? HypotheticalStrCount);

/// <param name="FiscalCode">Codice fiscale of the taxpayer (16 characters; spaces ignored), or null to clear it.</param>
public record SetPropertyTaxpayerRequest([StringLength(32)] string? FiscalCode);
