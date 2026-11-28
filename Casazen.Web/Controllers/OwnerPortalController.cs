using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/owner-portal")]
[Authorize(Policy = CasazenPolicies.OwnerPortal)]
public class OwnerPortalController(
    IOwnerPortalService ownerPortalService,
    IOptions<Auth0Options> auth0Options,
    ILogger<OwnerPortalController> logger) : ControllerBase
{
    private readonly string? _customClaimNamespace = auth0Options.Value.CustomClaimNamespace;

    [HttpGet("properties")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerPortalPropertyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<OwnerPortalPropertyDto>>> GetProperties(
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var properties = await ownerPortalService.GetOwnerPropertiesAsync(
            ownerUserId.Value,
            cancellationToken);

        var dtos = properties.Select(p => new OwnerPortalPropertyDto
        {
            PropertyId = p.PropertyId,
            PropertyName = p.PropertyName,
            Address = p.Address,
            TotalBookings = p.TotalBookings,
            TotalRevenue = p.TotalRevenue,
            OccupancyRate = p.OccupancyRate,
            RecentBookings = p.RecentBookings.Select(b => new OwnerPortalBookingSummaryDto
            {
                BookingId = b.BookingId,
                CheckInDate = b.CheckInDate,
                CheckOutDate = b.CheckOutDate,
                Channel = b.Channel,
                TotalAmount = b.TotalAmount,
                Status = b.Status
            }).ToList()
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// The published statements of the owner (E13/F01/US02/T02): each with its period, version, currency, publication day and totals (gross, channel commissions,
    /// withholding, commission, charges, net), the newest first; a version a later one replaced is marked <c>superseded</c>. Only the owner of the <c>owner_id</c> claim,
    /// and a delegated contact that holds <c>accounting.read</c> (403 <c>owner_portal_permission_missing</c> otherwise). Behind <c>Features:AgencyTier</c>: with the flag
    /// off the list stays empty, as it was. Never a guest.
    /// </summary>
    [HttpGet("statements")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerPortalStatementDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerPortalStatementDto>>> GetStatements(
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var statements = await ownerPortalService.GetOwnerStatementsAsync(
            ownerUserId.Value,
            cancellationToken);

        return Ok(statements.Select(OwnerPortalStatementDto.From).ToList());
    }

    [HttpGet("statements/{statementId:guid}")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(OwnerPortalStatementDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerPortalStatementDetailDto>> GetStatement(
        Guid statementId,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var detail = await ownerPortalService.GetOwnerStatementAsync(
            ownerUserId.Value,
            statementId,
            cancellationToken);

        return Ok(OwnerPortalStatementDetailDto.From(detail));
    }

    [HttpGet("statements/{statementId:guid}/pdf")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalDownload)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatementPdf(
        Guid statementId,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        return OwnerPortalFiles.Download(
            this,
            await ownerPortalService.OpenOwnerStatementPdfAsync(ownerUserId.Value, statementId, cancellationToken));
    }

    [HttpGet("invoices")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerPortalInvoiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerPortalInvoiceDto>>> GetInvoices(
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var invoices = await ownerPortalService.GetOwnerInvoicesAsync(
            ownerUserId.Value,
            cancellationToken);

        return Ok(invoices.Select(OwnerPortalInvoiceDto.From).ToList());
    }

    [HttpGet("invoices/{invoiceId:guid}/pdf")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalDownload)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoicePdf(
        Guid invoiceId,
        [FromQuery] string? lang,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        return OwnerPortalFiles.Download(
            this,
            await ownerPortalService.OpenOwnerInvoicePdfAsync(ownerUserId.Value, invoiceId, lang, cancellationToken));
    }

    /// <summary>
    /// The documents CasaZen issued in the name of the owner (E17/F02/US01/T01, NF-116): the receipts of a private owner and the invoices of a company, with their credit notes, issued on the
    /// issuer profile of an owner the caller reads the accounting of: number, day, kind, total, currency, state of the SDI and whether it has an XML, the newest first; <c>period</c>
    /// (<c>yyyy-MM</c> or <c>yyyy</c>) narrows them to the ones issued then (400 <c>accountant_package_invalid_period</c>). Only the owner of the <c>owner_id</c> claim, and a delegated contact
    /// that holds <c>accounting.read</c> (403 <c>owner_portal_permission_missing</c> otherwise). Behind <c>Features:AgencyTier</c> and <c>Features:FiscalDocumentIssuance</c>: with either off the
    /// list stays empty. Nothing of a guest is in the list: the PDF and the XML hold what the document itself says.
    /// </summary>
    [HttpGet("issued-documents")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerPortalIssuedDocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerPortalIssuedDocumentDto>>> GetIssuedDocuments(
        [FromQuery] string? period,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        AccountantPackagePeriod? asked = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            if (!AccountantPackagePeriod.TryParse(period, out var parsed))
                return this.ApiProblem(StatusCodes.Status400BadRequest, AccountantPackageErrors.InvalidPeriod, "AccountantPackageInvalidPeriod");

            asked = parsed;
        }

        var documents = await ownerPortalService.GetOwnerIssuedDocumentsAsync(
            ownerUserId.Value,
            asked,
            cancellationToken);

        return Ok(documents.Select(OwnerPortalIssuedDocumentDto.From).ToList());
    }

    [HttpGet("issued-documents/{documentId:guid}/pdf")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalDownload)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetIssuedDocumentPdf(
        Guid documentId,
        [FromQuery] string? lang,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        return OwnerPortalFiles.Download(
            this,
            await ownerPortalService.OpenOwnerIssuedDocumentPdfAsync(ownerUserId.Value, documentId, lang, cancellationToken));
    }

    /// <summary>
    /// The FatturaPA XML of one invoice or credit note issued in the name of the owner, as it was stored at the issue. 404 as the PDF, and <c>fiscal_document_xml_unavailable</c> for a document of
    /// the owner that has none (a receipt); 403 as the list.
    /// </summary>
    [HttpGet("issued-documents/{documentId:guid}/xml")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalDownload)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetIssuedDocumentXml(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var file = await ownerPortalService.OpenOwnerIssuedDocumentXmlAsync(ownerUserId.Value, documentId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("issued-documents/export")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalDownload)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportIssuedDocuments(
        [FromQuery] string? period,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        if (!AccountantPackagePeriod.TryParse(period, out var parsed))
            return this.ApiProblem(StatusCodes.Status400BadRequest, AccountantPackageErrors.InvalidPeriod, "AccountantPackageInvalidPeriod");

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        ZipDownloadHeaders.WhenTheResponseStarts(Response, OwnerIssuedDocumentsStructure.FileNameOf(parsed));
        var result = await ownerPortalService.ExportOwnerIssuedDocumentsAsync(ownerUserId.Value, parsed, Response.Body, cancellationToken);

        logger.LogInformation(
            "Owner issued documents downloaded from the portal. OwnerUserId={OwnerUserId} Documents={Documents} Files={Files} Bytes={Bytes}",
            ownerUserId.Value,
            result.Documents,
            result.Files,
            result.Bytes);
        return new EmptyResult();
    }

    [HttpGet("withholding")]
    [ProducesResponseType(typeof(OwnerPortalWithholdingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerPortalWithholdingDto>> GetWithholding(
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var withholding = await ownerPortalService.GetOwnerWithholdingAsync(
            ownerUserId.Value,
            year,
            cancellationToken);

        var dto = new OwnerPortalWithholdingDto
        {
            Year = withholding.Year,
            Currency = withholding.Currency,
            TotalWithheld = withholding.TotalWithheld,
            Details = withholding.Details.Select(d => new OwnerPortalWithholdingDetailDto
            {
                AgencyId = d.AgencyId,
                AgencyName = d.AgencyName,
                PropertyId = d.PropertyId,
                PropertyName = d.PropertyName,
                Month = d.Month,
                GrossBase = d.GrossBase,
                AmountWithheld = d.AmountWithheld
            }).ToList(),
            CuStatements = withholding.CuStatements.Select(c => new OwnerPortalCuDto
            {
                AgencyId = c.AgencyId,
                AgencyName = c.AgencyName,
                Year = c.Year,
                State = c.State,
                GeneratedAt = c.GeneratedAt,
                GrossBase = c.GrossBase,
                Withheld = c.Withheld,
                TaxCode = c.TaxCode,
                Attribution = c.Attribution
            }).ToList()
        };

        return Ok(dto);
    }

    [HttpGet("overview")]
    [ProducesResponseType(typeof(OwnerPortalOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetOverview(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? groupBy,
        [FromQuery] string? compare,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        if (this.CheckPerformanceRequest(fromDay, toDay, groupBy, compare, null, null, false, out var request) is { } problem)
            return problem;

        var overview = await ownerPortalService.GetOwnerOverviewAsync(
            ownerUserId.Value,
            new OwnerOverviewQuery(request.From, request.To, request.GroupBy, request.CompareYear) { PropertyId = propertyId },
            cancellationToken);
        return Ok(OwnerPortalOverviewDto.From(overview, request.GroupBy, HostReportDocuments.PerformanceTitle(request.GroupBy)));
    }

    [HttpGet("trend")]
    [ProducesResponseType(typeof(OwnerPortalTrendDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetTrend(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? groupBy,
        [FromQuery] string? compare,
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        if (this.CheckPerformanceRequest(fromDay, toDay, groupBy, compare, null, null, false, out var request) is { } problem)
            return problem;

        var trend = await ownerPortalService.GetOwnerTrendAsync(
            ownerUserId.Value,
            new OwnerOverviewQuery(request.From, request.To, request.GroupBy, request.CompareYear) { PropertyId = propertyId },
            cancellationToken);
        return Ok(OwnerPortalTrendDto.From(trend, request.GroupBy, HostReportDocuments.TrendTitle(request.GroupBy)));
    }
}
