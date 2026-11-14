using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The fiscal documents of the bookings (FIS-03, FIS-04, NF-83, E06/F01/US01/T01): the list and the detail of every document of the organization (the
/// ones imported from Octorate included), and the drafts, issue, credit notes, XML and PDF of the documents CasaZen issues for its direct bookings.
/// Reads need <c>payment.read</c>, every change <c>payment.write</c>; a single document is authorized through its booking like the payments of a
/// booking, a document without a booking only for a caller who reaches the whole org; a document of another org is a 404, like a missing one. The
/// changes are behind <c>Features:FiscalDocumentIssuance</c> (404 while off); the reads are not, because they also list the documents of Octorate.
/// </summary>
[ApiController]
[Route("api/fiscal/documents")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class FiscalDocumentsController(
    IFiscalDocumentService documents,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<FiscalDocumentsController> logger) : ControllerBase
{
    /// <summary>
    /// One page of the documents the caller reaches, newest first, narrowed in SQL to its scope. Filters: <c>bookingId</c>, <c>propertyId</c> (404 when the
    /// caller may not read their payments), <c>from</c>/<c>to</c> (Europe/Rome days of the issue), <c>kind</c>, <c>status</c>, <c>source</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(FiscalDocumentPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FiscalDocumentPageDto>> List(
        [FromQuery] Guid? bookingId = null,
        [FromQuery] Guid? propertyId = null,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery] FiscalDocumentKind? kind = null,
        [FromQuery] FiscalDocumentStatus? status = null,
        [FromQuery] FiscalDocumentSource? source = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = FiscalDocumentPage.DefaultPageSize)
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct) is not { } scope)
            return Unauthorized();

        if (bookingId is { } booking && !await CanOnBookingAsync(booking, PaymentOperations.Read, ct))
            return NotFound();
        if (propertyId is { } property
            && (await hostResources.ForPropertyAsync(property, ct) is not { } resource
                || !await authorizationService.IsAuthorizedAsync(User, resource, PaymentOperations.Read)))
        {
            return NotFound();
        }

        var result = await documents.ListAsync(scope, new FiscalDocumentCriteria(bookingId, propertyId, fromDay, toDay, kind, status, source), page, pageSize, ct);
        return Ok(new FiscalDocumentPageDto(result.Items.Select(FiscalDocumentDto.From).ToList(), result.TotalCount, result.Page, result.PageSize));
    }

    /// <summary>One document with its lines.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(FiscalDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FiscalDocumentDto>> Get(Guid id)
    {
        var document = await ReachableAsync(id, PaymentOperations.Read);
        return document is null ? DocumentNotFound() : Ok(FiscalDocumentDto.From(document));
    }

    /// <summary>
    /// The draft of a direct booking (or the one it has, made again): 422 <c>document_booking_not_direct</c>, <c>document_invoice_data_missing</c>,
    /// <c>document_rule_not_configured</c>, <c>issuer_profile_incomplete</c>; 409 <c>document_issuer_conflict</c>, <c>document_already_issued</c>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [ProducesResponseType(typeof(FiscalDocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentDto>> CreateDraft([FromBody] CreateFiscalDocumentRequest request)
    {
        var ct = HttpContext.RequestAborted;
        if (!await CanOnBookingAsync(request.BookingId, PaymentOperations.Write, ct))
            return NotFound();

        var draft = await documents.CreateDraftAsync(request.BookingId, User.FindFirst("sub")?.Value, ct);
        logger.LogInformation("Fiscal document draft {DocumentId} of booking {BookingId}", draft.Id, request.BookingId);
        return CreatedAtAction(nameof(Get), new { id = draft.Id }, FiscalDocumentDto.From(draft));
    }

    /// <summary>The draft made again from the current price and data of its booking. 422 <c>document_not_draft</c>, <c>document_read_only</c>.</summary>
    [HttpPost("{id:guid}/refresh")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [ProducesResponseType(typeof(FiscalDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentDto>> Refresh(Guid id)
    {
        if (await ReachableAsync(id, PaymentOperations.Write) is null)
            return DocumentNotFound();

        return Ok(FiscalDocumentDto.From(await documents.RefreshDraftAsync(id, HttpContext.RequestAborted)));
    }

    /// <summary>
    /// The draft issued, after the host's confirmation (<c>{ confirm: true }</c>, else 422 <c>document_confirmation_required</c>): number, validated XML,
    /// state <c>to_send</c>. 422 <c>fatturapa_xml_invalid</c>, <c>fatturapa_schema_unavailable</c>, <c>series_too_long</c>; 409 <c>document_draft_outdated</c> when
    /// the tax rule or the data of the issuer changed since the draft was made (refresh it first).
    /// </summary>
    [HttpPost("{id:guid}/issue")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [ProducesResponseType(typeof(FiscalDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentDto>> Issue(Guid id, [FromBody] IssueFiscalDocumentRequest request)
    {
        if (await ReachableAsync(id, PaymentOperations.Write) is null)
            return DocumentNotFound();

        return Ok(FiscalDocumentDto.From(await documents.IssueAsync(id, request.Confirm, HttpContext.RequestAborted)));
    }

    /// <summary>
    /// A credit note of an issued CasaZen document, issued at once in its series: <c>{ amount?, reason, confirm }</c>, the whole document by default.
    /// 422 <c>credit_amount_exceeds</c>, <c>document_not_creditable</c>, <c>document_read_only</c>, <c>document_confirmation_required</c>.
    /// </summary>
    [HttpPost("{id:guid}/credit-note")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [ProducesResponseType(typeof(FiscalDocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentDto>> CreditNote(Guid id, [FromBody] CreateCreditNoteRequest request)
    {
        if (await ReachableAsync(id, PaymentOperations.Write) is null)
            return DocumentNotFound();

        var note = await documents.CreateCreditNoteAsync(id, request.Amount, request.Reason, request.Confirm, User.FindFirst("sub")?.Value, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(Get), new { id = note.Id }, FiscalDocumentDto.From(note));
    }

    /// <summary>The draft discarded (no number consumed). 422 <c>document_not_draft</c>, <c>document_read_only</c>.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Discard(Guid id)
    {
        if (await ReachableAsync(id, PaymentOperations.Write) is null)
            return DocumentNotFound();

        await documents.DiscardDraftAsync(id, HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>The FatturaPA XML of an issued CasaZen invoice or credit note. 404 <c>fiscal_document_xml_unavailable</c> for any other document.</summary>
    [HttpGet("{id:guid}/xml")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Xml(Guid id)
    {
        var document = await ReachableAsync(id, PaymentOperations.Read);
        if (document is null)
            return DocumentNotFound();

        var xml = await documents.GetXmlAsync(id, HttpContext.RequestAborted);
        return File(System.Text.Encoding.UTF8.GetBytes(xml), "application/xml", $"{FileNameOf(document)}.xml");
    }

    /// <summary>The PDF of a CasaZen document in the language of the request (the courtesy copy of an electronic invoice).</summary>
    [HttpGet("{id:guid}/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Pdf(Guid id)
    {
        var document = await ReachableAsync(id, PaymentOperations.Read);
        if (document is null)
            return DocumentNotFound();

        var pdf = await documents.GetPdfAsync(id, DocumentLanguages.Current, HttpContext.RequestAborted);
        return File(pdf, "application/pdf", $"{FileNameOf(document)}.pdf");
    }

    /// <summary>The document when the caller reaches it: through its booking, or the whole org for a document without a booking.</summary>
    private async Task<FiscalDocument?> ReachableAsync(Guid id, HostOperationRequirement operation)
    {
        var ct = HttpContext.RequestAborted;
        var document = await documents.GetAsync(id, ct);
        if (document is null)
            return null;

        if (document.BookingId is { } bookingId)
            return await CanOnBookingAsync(bookingId, operation, ct) ? document : null;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        return orgId is not null
               && document.OrgId == orgId.Value
               && await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct) is { IsOrgWide: true }
            ? document
            : null;
    }

    private async Task<bool> CanOnBookingAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken ct)
    {
        var booking = await hostResources.ForBookingAsync(bookingId, ct);
        return booking is not null && await authorizationService.IsAuthorizedAsync(User, booking, operation);
    }

    private ActionResult DocumentNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, FiscalDocumentErrors.NotFound, "FiscalDocumentNotFound");

    private static string FileNameOf(FiscalDocument document) =>
        "documento-" + (document.Number is { Length: > 0 } number ? number.Replace('/', '-') : document.Id.ToString("N"));
}
