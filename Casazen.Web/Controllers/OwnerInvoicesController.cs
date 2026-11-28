using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The invoices of the property manager to the owners (E14/F01/US01/T01, NF-114): the list and the read of one invoice by its id (<c>agency.statement.view</c>), the draft
/// made from a statement version, its issue, its discard and the credit note of an issued invoice (<c>agency.statement.manage</c>). The PDF and the XML of an owner invoice are the
/// ones of every fiscal document (<c>GET api/fiscal/documents/{id}/pdf</c> and <c>/xml</c>, a document without a booking: the whole org only).
/// </summary>
/// <remarks>
/// Behind <c>Features:OwnerInvoicing</c>: every endpoint answers 404 while it is off. The invoices of an owner span the properties of the owner, like the owner
/// accounting they come from: a caller restricted to some properties is refused (403) even with the permission. A version or an invoice of another org is 404.
/// The credit note and the discard are here and nowhere else: the endpoints of the guest documents (<c>api/fiscal/documents</c>, <c>payment.write</c>) answer 422
/// <c>document_owner_invoice</c> for an owner invoice, since a credit note writes to the owner ledger and a role that holds <c>payment.write</c> (the property
/// manager) holds no agency permission.
/// </remarks>
[ApiController]
[Route("api/owner-invoices")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
[FeatureGate(FeatureFlags.OwnerInvoicing)]
public class OwnerInvoicesController(
    IOwnerInvoiceService invoices,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<OwnerInvoiceListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<List<OwnerInvoiceListItemDto>>> List([FromQuery] string? period = null, [FromQuery] Guid? ownerId = null)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        int? year = null, month = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            if (!DateOnly.TryParseExact(period.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OwnerAccountingErrors.PeriodInvalid, "OwnerPeriodInvalid");
            (year, month) = (first.Year, first.Month);
        }

        var rows = await invoices.ListAsync(orgId, new OwnerInvoiceCriteria(year, month, ownerId), HttpContext.RequestAborted);
        return Ok(rows.Select(OwnerInvoiceListItemDto.From).ToList());
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OwnerInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerInvoiceDto>> Get(Guid id)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        return Ok(OwnerInvoiceDto.From(await invoices.GetAsync(orgId, id, HttpContext.RequestAborted)));
    }

    [HttpPost("drafts")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerInvoiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerInvoiceDto>> CreateDraft([FromBody] CreateOwnerInvoiceDraftRequest request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        var draft = await invoices.CreateDraftAsync(orgId, request.VersionId, User.GetUserId(), HttpContext.RequestAborted);
        return Created($"/api/fiscal/documents/{draft.Id}", OwnerInvoiceDto.From(draft));
    }

    /// <summary>
    /// The draft issued after the confirmation (<c>{ confirm: true }</c>, else 422 <c>document_confirmation_required</c>): numbered in the owner-invoice series,
    /// its VAT on top posted once to the owner ledger. 404 for an invoice of another org; 422 <c>document_not_draft</c>, <c>fatturapa_schema_unavailable</c>
    /// (an Italian invoice until the official schema is in the build); 409 <c>document_draft_outdated</c> (make the draft again first).
    /// </summary>
    [HttpPost("{id:guid}/issue")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerInvoiceDto>> Issue(Guid id, [FromBody] IssueFiscalDocumentRequest request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        var issued = await invoices.IssueAsync(orgId, id, request.Confirm, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(OwnerInvoiceDto.From(issued));
    }

    /// <summary>
    /// A credit note of an issued owner invoice (<c>{ amount?, reason, confirm }</c>, the whole invoice by default), issued at once in the series of the invoice:
    /// it gives the VAT the invoice posted back to the owner ledger in the same transaction. 404 for an invoice of another org or a document that is not an owner
    /// invoice; 422 <c>document_confirmation_required</c>, <c>document_not_creditable</c>, <c>credit_amount_exceeds</c>.
    /// </summary>
    [HttpPost("{id:guid}/credit-note")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerInvoiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerInvoiceDto>> CreditNote(Guid id, [FromBody] CreateCreditNoteRequest request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        var note = await invoices.CreateCreditNoteAsync(orgId, id, request.Amount, request.Reason, request.Confirm, User.GetUserId(), HttpContext.RequestAborted);
        return Created($"/api/fiscal/documents/{note.Id}", OwnerInvoiceDto.From(note));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Discard(Guid id)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        await invoices.DiscardDraftAsync(orgId, id, HttpContext.RequestAborted);
        return NoContent();
    }

    private Task<Guid?> OrgAsync() =>
        HostCallerResolver.OrgWideAsync(User, orgContextResolver, hostScopeResolver, HttpContext.RequestAborted);
}
