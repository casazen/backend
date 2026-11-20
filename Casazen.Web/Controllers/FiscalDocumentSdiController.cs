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
/// The Italian electronic invoices of CasaZen on their way to the SDI (E17/F01/US02/T01 and T03, NF-116, NF-86): the state of a document (<c>GET</c>), its submission through the channel
/// (<c>POST submit</c>), the declaration of a file the issuer sent by hand (<c>POST declare</c>) and the correction of a rejected invoice (<c>POST correct</c>). Reads need <c>payment.read</c>,
/// every change <c>payment.write</c>; a document is authorized through its booking like the payments of a booking, a document without a booking only for a caller who reaches the whole org;
/// a document of another org is a 404, like a missing one. An invoice of the property manager to an owner is never read or changed here (422 <c>document_owner_invoice</c>): its SDI state, its
/// submission and its declaration are on <see cref="OwnerInvoiceSdiController"/>, behind <c>agency.statement.manage</c> and <c>Features:OwnerInvoicing</c>, since a role that holds
/// <c>payment.write</c> (the property manager) holds no agency permission. A self-billed invoice (TD17) is sent and declared here like any electronic invoice, but it is not corrected here
/// (422 <c>self_billing_not_correctable</c>): its rejection is read, and it is corrected (<c>POST api/fiscal/self-billing/{id}/correct</c>, behind <c>agency.invoice.manage</c>), on the self-billed invoices.
/// </summary>
/// <remarks>
/// The three changes are behind <c>Features:SdiSubmission</c> <b>and</b> <c>Features:FiscalDocumentIssuance</c> (404 while either is off); the state is a read and is not. With no
/// provider configured <c>submit</c> answers <c>NotConfigured</c> and changes nothing, and <c>declare</c> works all the same. The words of a reason are those of the request's language
/// and never name the provider (decision D14).
/// </remarks>
[ApiController]
[Route("api/fiscal/documents/{id:guid}/sdi")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class FiscalDocumentSdiController(
    IFiscalDocumentService documents,
    IFiscalDocumentSdiService sdi,
    IFiscalDocumentCorrectionService corrections,
    ISdiEInvoiceProvider provider,
    IDocumentTexts texts,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<FiscalDocumentSdiController> logger) : ControllerBase
{
    /// <summary>
    /// The SDI state of the document: <c>{ documentId, status, errorCode?, reason?, statusUpdatedAt?, providerConfigured, canSubmit, canDeclare, canCorrect }</c>, the reason in plain words
    /// (a rejected document always has one). 404 <c>fiscal_document_not_found</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(FiscalDocumentSdiDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FiscalDocumentSdiDto>> Get(Guid id)
    {
        if (await ReachableAsync(id, PaymentOperations.Read) is not { } document)
            return DocumentNotFound();
        if (IsOwnerInvoice(document))
            return OwnerInvoiceRefused();

        var state = await sdi.GetStateAsync(id, HttpContext.RequestAborted);
        return state is null ? DocumentNotFound() : Ok(FiscalDocumentSdiDto.From(state, texts, DocumentLanguages.Current));
    }

    /// <summary>
    /// Submits an issued invoice in state <c>to_send</c> to the SDI through the channel: <c>{ outcome, state }</c>, the outcome being <c>Submitted</c> (the channel accepted the file), <c>Refused</c>
    /// (it stays to send, with the code and the words of why), <c>AlreadySubmitted</c> or <c>NotConfigured</c> (no channel is connected: nothing changed, upload the XML by hand and declare it).
    /// 404 <c>fiscal_document_not_found</c>; 422 <c>document_read_only</c>, <c>sdi_not_electronic</c>; 409 <c>sdi_not_to_send</c>.
    /// </summary>
    [HttpPost("submit")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [FeatureGate(FeatureFlags.SdiSubmission)]
    [ProducesResponseType(typeof(SubmitFiscalDocumentSdiResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SubmitFiscalDocumentSdiResultDto>> Submit(Guid id)
    {
        if (await ReachableAsync(id, PaymentOperations.Write) is not { } document)
            return DocumentNotFound();
        if (IsOwnerInvoice(document))
            return OwnerInvoiceRefused();

        var ct = HttpContext.RequestAborted;
        var result = await sdi.SubmitAsync(id, ct);
        logger.LogInformation("Fiscal document {DocumentId}: SDI submission asked by the host, outcome {Outcome}", id, result.Outcome);
        var state = await sdi.GetStateAsync(id, ct);
        return state is null ? DocumentNotFound() : Ok(new SubmitFiscalDocumentSdiResultDto(result.Outcome.ToString(), FiscalDocumentSdiDto.From(state, texts, DocumentLanguages.Current)));
    }

    /// <summary>
    /// The issuer sent the file through its own channel: <c>{ kind: Sent, date, reference }</c> (the document becomes <c>manual_declared</c>) or <c>{ kind: Rejected, errorCode, errorText? }</c> (the code and
    /// the text of the notification of rejection the SDI sent). Works with no channel connected. Answers the state. 404; 422 <c>sdi_declaration_invalid</c> (with the <c>fields</c>),
    /// <c>document_read_only</c>, <c>sdi_not_electronic</c>; 409 <c>sdi_state_final</c> (delivered), <c>sdi_not_to_send</c> (submitted through the channel: its answer decides).
    /// </summary>
    [HttpPost("declare")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [FeatureGate(FeatureFlags.SdiSubmission)]
    [ProducesResponseType(typeof(FiscalDocumentSdiDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentSdiDto>> Declare(Guid id, [FromBody] DeclareFiscalDocumentSdiRequest request)
    {
        if (await ReachableAsync(id, PaymentOperations.Write) is not { } document)
            return DocumentNotFound();
        if (IsOwnerInvoice(document))
            return OwnerInvoiceRefused();

        // A declaration with no kind is the one invalid field the service names (it never guesses what was meant).
        var declaration = new SdiDeclaration(request.Kind ?? default, request.Date, request.Reference, request.ErrorCode, request.ErrorText);
        var state = await sdi.DeclareManuallyAsync(id, declaration, HttpContext.RequestAborted);
        logger.LogInformation("Fiscal document {DocumentId}: SDI state declared by the host ({Kind})", id, declaration.Kind);
        return Ok(FiscalDocumentSdiDto.From(state, texts, DocumentLanguages.Current));
    }

    /// <summary>
    /// Starts the correction of an invoice the SDI rejected: answers the replacement draft (<c>replacesDocumentId</c> names the rejected document), which keeps its series and number, to be fixed
    /// (the invoice data of the booking, then refresh) and issued. 404; 422 <c>document_not_rejected</c> (a delivered document is corrected with a credit note), <c>document_not_correctable</c>,
    /// <c>document_read_only</c>, <c>document_owner_invoice</c>, <c>self_billing_not_correctable</c> (a self-billed invoice, TD17: no correction is made for it here, whatever its state, and no «Da fare» to correct
    /// it is raised: its correction is <c>POST api/fiscal/self-billing/{id}/correct</c>). A second start answers the same draft.
    /// </summary>
    [HttpPost("correct")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [FeatureGate(FeatureFlags.SdiSubmission)]
    [ProducesResponseType(typeof(FiscalDocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentDto>> Correct(Guid id)
    {
        if (await ReachableAsync(id, PaymentOperations.Write) is not { } document)
            return DocumentNotFound();
        if (IsOwnerInvoice(document))
            return OwnerInvoiceRefused();
        if (IsSelfBilling(document))
            return SelfBillingNotCorrectable();

        var replacement = await corrections.StartCorrectionAsync(id, User.FindFirst("sub")?.Value, HttpContext.RequestAborted);
        logger.LogInformation("Fiscal document {DocumentId}: correction started by the host, replacement draft {ReplacementId}", id, replacement.Id);
        var dto = FiscalDocumentDto.From(replacement);
        return CreatedAtAction(nameof(FiscalDocumentsController.Get), "FiscalDocuments", new { id = replacement.Id }, dto);
    }

    /// <summary>
    /// The issuers of the organization that send electronic invoices to the SDI and where each stands with the channel: <c>{ providerConfigured, issuers: [{ issuerProfileId, propertyOwnerId?, state }] }</c>,
    /// <c>state</c> being <c>registered</c>, <c>pending</c> (its VAT number is not enabled at the channel yet) or <c>unknown</c> (nothing was submitted yet). Behind <c>Features:FiscalDocumentIssuance</c>.
    /// A caller who reaches only some properties («Solo alcuni», an owner) sees only the issuers that have a document on a booking of those properties.
    /// </summary>
    [HttpGet("~/api/fiscal/sdi/issuers")]
    [FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
    [ProducesResponseType(typeof(SdiIssuersDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SdiIssuersDto>> Issuers()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct) is not { } scope)
            return Unauthorized();

        var issuers = await sdi.IssuerRegistrationsAsync(scope, ct);
        return Ok(new SdiIssuersDto(provider.IsConfigured, issuers.Select(i => new SdiIssuerRegistrationDto(i.IssuerProfileId, i.PropertyOwnerId, i.State)).ToList()));
    }

    /// <summary>The document when the caller reaches it: through its booking, or the whole org for a document without a booking (as <see cref="FiscalDocumentsController"/>).</summary>
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

    /// <summary>An invoice of the property manager to an owner: its SDI state and actions are those of <see cref="OwnerInvoiceSdiController"/> (its permission, its flag).</summary>
    private static bool IsOwnerInvoice(FiscalDocument document) => document.Purpose == FiscalDocumentPurpose.OwnerInvoice;

    private ActionResult OwnerInvoiceRefused() =>
        this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OwnerInvoiceErrors.UseOwnerInvoices, "FiscalDocumentOwnerInvoice");

    /// <summary>A self-billed invoice (TD17): it is sent to the SDI like every electronic invoice, but it has no correction here (FIX/RES-DOCS).</summary>
    private static bool IsSelfBilling(FiscalDocument document) => document.Purpose == FiscalDocumentPurpose.SelfBilling;

    private ActionResult SelfBillingNotCorrectable() =>
        this.ApiProblem(StatusCodes.Status422UnprocessableEntity, SelfBillingErrors.NotCorrectable, "SelfBillingNotCorrectable");
}
