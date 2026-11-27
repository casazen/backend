using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The list of the fiscal documents as a CSV or an Excel file (FIS-07, NF-87, E06/F03/US03/T01): the same documents as <c>GET api/fiscal/documents</c>
/// (the ones CasaZen issued and the ones imported from Octorate), the issued ones only, narrowed in SQL to the caller's scope, with the same fourteen
/// columns in both formats. Needs <c>payment.read</c>; <c>bookingId</c> and <c>propertyId</c> are authorized like the payments they belong to (404
/// otherwise), as in the list. The file is built on request, never stored, and the answer is <c>Cache-Control: no-store</c>. The single documents
/// download as PDF and XML from <see cref="FiscalDocumentsController"/>; the monthly accountant package (NF-101, epic E07) reuses
/// <see cref="XlsxWriter"/> and these columns.
/// </summary>
[ApiController]
[Route("api/fiscal/documents/export")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class FiscalDocumentExportsController(
    IFiscalDocumentService documents,
    IDocumentTexts texts,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    TimeProvider timeProvider,
    ILogger<FiscalDocumentExportsController> logger) : ControllerBase
{
    public const string InvalidFormatCode = "fiscal_export_invalid_format";

    public const string InvalidRangeCode = "fiscal_export_invalid_range";

    [HttpGet]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Export(
        [FromQuery] string? format = null,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery] FiscalDocumentKind? kind = null,
        [FromQuery] FiscalDocumentSource? source = null,
        [FromQuery] Guid? bookingId = null,
        [FromQuery] Guid? propertyId = null)
    {
        var ct = HttpContext.RequestAborted;
        if (FiscalDocumentExportFormats.Parse(format) is not { } fileFormat)
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidFormatCode, "FiscalExportInvalidFormat");
        if (!IsSupportedRange(fromDay, toDay))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidRangeCode, "FiscalExportInvalidRange");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct) is not { } scope)
            return Unauthorized();

        if (bookingId is { } booking && !await CanReadPaymentsOfBookingAsync(booking, ct))
            return NotFound();
        if (propertyId is { } property
            && (await hostResources.ForPropertyAsync(property, ct) is not { } resource
                || !await authorizationService.IsAuthorizedAsync(User, resource, PaymentOperations.Read)))
        {
            return NotFound();
        }

        var rows = await documents.ListForExportAsync(
            scope,
            new FiscalDocumentCriteria(bookingId, propertyId, fromDay, toDay, kind, Status: null, source),
            FiscalDocumentExports.Limit,
            ct);

        var language = DocumentLanguages.Current;
        var content = fileFormat == FiscalDocumentExportFormat.Xlsx
            ? FiscalDocumentExports.Xlsx(rows, texts, language)
            : FiscalDocumentExports.Csv(rows, texts, language);
        var month = fromDay ?? toDay ?? timeProvider.TodayInRomeAsDateOnly();

        logger.LogInformation("Fiscal documents exported as {Format}: {Count} rows", fileFormat, rows.Count);
        Response.Headers.CacheControl = "no-store";
        return File(
            content,
            FiscalDocumentExportFormats.ContentTypeOf(fileFormat),
            FiscalDocumentExports.FileName(month, FiscalDocumentExportFormats.ExtensionOf(fileFormat)));
    }

    private static bool IsSupportedRange(DateOnly? fromDay, DateOnly? toDay) =>
        (fromDay is not { } from || from.Year >= HostDashboardPeriod.MinCustomYear && from.Year <= HostDashboardPeriod.MaxCustomYear)
        && (toDay is not { } to || to.Year >= HostDashboardPeriod.MinCustomYear && to.Year <= HostDashboardPeriod.MaxCustomYear)
        && (fromDay is not { } first || toDay is not { } last || first <= last);

    private async Task<bool> CanReadPaymentsOfBookingAsync(Guid bookingId, CancellationToken ct)
    {
        var booking = await hostResources.ForBookingAsync(bookingId, ct);
        return booking is not null && await authorizationService.IsAuthorizedAsync(User, booking, PaymentOperations.Read);
    }
}
