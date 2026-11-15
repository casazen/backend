using Casazen.Core.Authorization;
using Casazen.Core.Documents;
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
/// The SDI state of an invoice of the property manager to an owner (E14/F01/US01/T01, E17/F01/US02/T01, NF-116): its state (<c>GET</c>), its submission through the channel (<c>POST submit</c>)
/// and the declaration of a file the agency sent by hand (<c>POST declare</c>), with the answers of <see cref="FiscalDocumentSdiController"/>. An owner invoice spans the properties of its owner and
/// belongs to the owner accounting, so it follows the rules of <see cref="OwnerInvoicesController"/>: <c>agency.statement.view</c> to read, <c>agency.statement.manage</c> to change, the whole
/// org only (403 for a caller restricted to some properties, before anything is read), behind <c>Features:OwnerInvoicing</c>; the changes are also behind <c>Features:FiscalDocumentIssuance</c>
/// and <c>Features:SdiSubmission</c>. A document that is not an owner invoice of the caller's org is 404 here: a guest document is read and sent on <c>api/fiscal/documents/{id}/sdi</c>, which
/// answers 422 <c>document_owner_invoice</c> for an owner invoice (a role that holds <c>payment.write</c>, the property manager, holds no agency permission).
/// </summary>
[ApiController]
[Route("api/owner-invoices/{id:guid}/sdi")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
[FeatureGate(FeatureFlags.OwnerInvoicing)]
public class OwnerInvoiceSdiController(
    IFiscalDocumentService documents,
    IFiscalDocumentSdiService sdi,
    IDocumentTexts texts,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<OwnerInvoiceSdiController> logger) : ControllerBase
{
    /// <summary>The SDI state of the owner invoice, the reason of a rejection or of a refusal in plain words (as <c>GET api/fiscal/documents/{id}/sdi</c>). 404 <c>fiscal_document_not_found</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(FiscalDocumentSdiDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FiscalDocumentSdiDto>> Get(Guid id)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        if (!await IsOwnerInvoiceOfAsync(orgId, id))
            return DocumentNotFound();

        var state = await sdi.GetStateAsync(id, HttpContext.RequestAborted);
        return state is null ? DocumentNotFound() : Ok(FiscalDocumentSdiDto.From(state, texts, DocumentLanguages.Current));
    }

    /// <summary>
    /// Submits the issued owner invoice in state <c>to_send</c> to the SDI through the channel: <c>{ outcome, state }</c> (<c>Submitted</c>, <c>Refused</c>, <c>AlreadySubmitted</c>,
    /// <c>NotConfigured</c>: no channel, nothing changed). 404; 422 <c>sdi_not_electronic</c>; 409 <c>sdi_not_to_send</c>.
    /// </summary>
    [HttpPost("submit")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [FeatureGate(FeatureFlags.SdiSubmission)]
    [ProducesResponseType(typeof(SubmitFiscalDocumentSdiResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SubmitFiscalDocumentSdiResultDto>> Submit(Guid id)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        if (!await IsOwnerInvoiceOfAsync(orgId, id))
            return DocumentNotFound();

        var ct = HttpContext.RequestAborted;
        var result = await sdi.SubmitAsync(id, ct);
        logger.LogInformation("Owner invoice {DocumentId}: SDI submission asked by the agency, outcome {Outcome}", id, result.Outcome);
        var state = await sdi.GetStateAsync(id, ct);
        return state is null ? DocumentNotFound() : Ok(new SubmitFiscalDocumentSdiResultDto(result.Outcome.ToString(), FiscalDocumentSdiDto.From(state, texts, DocumentLanguages.Current)));
    }

    /// <summary>
    /// The agency sent the file of the owner invoice through its own channel: <c>{ kind: Sent, date, reference }</c> or <c>{ kind: Rejected, errorCode, errorText? }</c>, as
    /// <c>POST api/fiscal/documents/{id}/sdi/declare</c>. Works with no channel connected. 404; 422 <c>sdi_declaration_invalid</c>, <c>sdi_not_electronic</c>; 409 <c>sdi_state_final</c>, <c>sdi_not_to_send</c>.
    /// </summary>
    [HttpPost("declare")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [FeatureGate(FeatureFlags.SdiSubmission)]
    [ProducesResponseType(typeof(FiscalDocumentSdiDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentSdiDto>> Declare(Guid id, [FromBody] DeclareFiscalDocumentSdiRequest request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        if (!await IsOwnerInvoiceOfAsync(orgId, id))
            return DocumentNotFound();

        // A declaration with no kind is the one invalid field the service names (it never guesses what was meant).
        var declaration = new SdiDeclaration(request.Kind ?? default, request.Date, request.Reference, request.ErrorCode, request.ErrorText);
        var state = await sdi.DeclareManuallyAsync(id, declaration, HttpContext.RequestAborted);
        logger.LogInformation("Owner invoice {DocumentId}: SDI state declared by the agency ({Kind})", id, declaration.Kind);
        return Ok(FiscalDocumentSdiDto.From(state, texts, DocumentLanguages.Current));
    }

    /// <summary>The caller's org, only when its reach is the whole org (as <see cref="OwnerInvoicesController"/>).</summary>
    private async Task<Guid?> OrgAsync()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
        return scope is { IsOrgWide: true } ? orgId : null;
    }

    /// <summary>The document is an invoice of the property manager to an owner, of <paramref name="orgId"/>.</summary>
    private async Task<bool> IsOwnerInvoiceOfAsync(Guid orgId, Guid id) =>
        await documents.GetAsync(id, HttpContext.RequestAborted) is { Purpose: FiscalDocumentPurpose.OwnerInvoice } document && document.OrgId == orgId;

    private ActionResult DocumentNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, FiscalDocumentErrors.NotFound, "FiscalDocumentNotFound");
}
