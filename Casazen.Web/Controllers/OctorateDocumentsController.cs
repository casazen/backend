using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// The PDF of a document Octorate issued (FIS-04, NF-84, E06/F03/US02/T01), served through CasaZen and never as an Octorate address. Needs
/// <c>payment.read</c>; the booking of the document is authorized like the payments of a booking, and a document without a booking only for a caller
/// who reaches the whole org. A document of another org is a 404, the same answer as a missing one. Behind <c>Features:OctorateConnector</c> and
/// <c>Features:OctorateDocumentImport</c>: the operation of the PDF is documented but never proven on the sandbox.
/// </summary>
[ApiController]
[Route("api/fiscal/documents")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class OctorateDocumentsController(
    AppDbContext db,
    IOctorateApiClient octorate,
    IOctorateOrgScope orgScope,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<OctorateDocumentsController> logger) : ControllerBase
{
    /// <summary>
    /// The PDF of an imported document. 404 <c>fiscal_document_not_found</c> for a document that is not one of the caller's org, not imported from
    /// Octorate or not reachable by the caller; 404 <c>octorate_pdf_unavailable</c> when Octorate holds no PDF of it or cannot be read for it (the
    /// reason is in the log, with ids only).
    /// </summary>
    [HttpGet("{id:guid}/octorate-pdf")]
    [FeatureGate(FeatureFlags.OctorateConnector)]
    [FeatureGate(FeatureFlags.OctorateDocumentImport)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetOctoratePdf(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct) is not { } scope)
            return Forbid();

        var document = await db.FiscalDocuments
            .AsNoTracking()
            .Where(d => d.Id == id && d.OrgId == orgId.Value && d.Source == FiscalDocumentSource.Octorate)
            .Select(d => new { d.Id, d.BookingId, d.OctorateDocumentId, d.OctorateAccommodationId, d.OctoratePdfAvailable, d.Number })
            .FirstOrDefaultAsync(ct);
        if (document is null || !await CanReadAsync(document.BookingId, scope, ct))
            return this.ApiProblem(StatusCodes.Status404NotFound, OctorateDocumentErrors.DocumentNotFound, "FiscalDocumentNotFound");

        // The accommodation the import read the document from; for a document imported before it was stored, the one of its booking's property.
        var accommodation = !string.IsNullOrWhiteSpace(document.OctorateAccommodationId)
            ? document.OctorateAccommodationId
            : document.BookingId is { } bookingId ? await AccommodationOfAsync(orgId.Value, bookingId, ct) : null;
        if (!document.OctoratePdfAvailable || accommodation is null || string.IsNullOrWhiteSpace(document.OctorateDocumentId))
            return this.ApiProblem(StatusCodes.Status404NotFound, OctorateDocumentErrors.PdfUnavailable, "OctoratePdfUnavailable");

        try
        {
            using (orgScope.Use(orgId.Value))
            {
                var pdf = await octorate.GetDocumentPdfAsync(accommodation, document.OctorateDocumentId, ct);
                return File(pdf, "application/pdf", $"documento-{document.Id:N}.pdf");
            }
        }
        catch (OctorateException ex)
        {
            logger.LogWarning("PDF of the Octorate document {DocumentId} not read ({Error})", document.Id, ex.GetType().Name);
            return this.ApiProblem(StatusCodes.Status404NotFound, OctorateDocumentErrors.PdfUnavailable, "OctoratePdfUnavailable");
        }
    }

    /// <summary>The booking of the document, authorized like the payments of a booking; a document without a booking only for the whole org.</summary>
    private async Task<bool> CanReadAsync(Guid? bookingId, HostScope scope, CancellationToken ct)
    {
        if (bookingId is not { } id)
            return scope.IsOrgWide;

        var booking = await hostResources.ForBookingAsync(id, ct);
        return booking is not null && await authorizationService.IsAuthorizedAsync(User, booking, PaymentOperations.Read);
    }

    /// <summary>The accommodation of the booking's property on Octorate (its confirmed mapping); <c>null</c> when there is none.</summary>
    private Task<string?> AccommodationOfAsync(Guid orgId, Guid bookingId, CancellationToken ct) =>
        db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .SelectMany(b => db.OctorateListingMappings.Where(m =>
                m.OrgId == orgId && m.PropertyId == b.PropertyId && m.Status == OctorateMappingStatus.Confirmed))
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(ct);
}
