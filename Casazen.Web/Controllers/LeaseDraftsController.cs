using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The guided draft of a lease (LR-12): a draft that exists from the first step of the wizard, saved step by step, confirmed after a
/// review, marked as downloaded for the signature on paper, and deleted. Every endpoint needs <c>lease.create</c>; each is
/// authorized as a <see cref="HostResource"/> of the property of the lease (TN-3, AM-03): another org's lease or property is 404, one
/// the caller may not handle is 403, so a member limited to some properties neither creates nor reads the drafts of the others. The
/// complete creation <c>POST api/leases</c> and the masked reads of <see cref="LeasesController"/> are not changed by this.
/// </summary>
/// <remarks>
/// The answers of the draft carry personal data in clear (<c>GET draft</c>: names, fiscal codes, residences): <c>Cache-Control:
/// no-store</c>, never logged. Errors are RFC 7807 problems with a stable <c>code</c>; a value that is wrong is <b>not</b> an error
/// here, it is saved and reported in <c>flow</c> (the errors are the wrong shape, an old version, a lease that is not a draft).
/// </remarks>
[ApiController]
[Route("api/leases")]
[Authorize(Policy = CasazenPolicies.LeaseCreate)]
public class LeaseDraftsController(
    ILeaseDraftService drafts,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    private const string LeaseNotFoundCode = "lease_not_found";
    private const string PropertyNotFoundCode = "property_not_found";

    /// <summary>The values of one save: a few kilobytes at most (ten landlords and ten tenants of a few hundred characters each).</summary>
    private const long SaveRequestLimit = 256 * 1024;

    /// <summary>The signed contract (at most <see cref="LeaseSigningLimits.MaxSignedContractBytes"/>) plus the other form fields.</summary>
    private const long ImportRequestLimit = LeaseSigningLimits.MaxSignedContractBytes + 64 * 1024;

    /// <summary>
    /// Creates a draft with only the property: 201 with the id, the version (1) and the flow (every step to do). No APE, no
    /// parties, no dates are asked yet. <c>origin</c> is <c>Created</c> (the default) or <c>Imported</c> (a contract already signed
    /// elsewhere, LR-13). 404 <c>property_not_found</c> for a property of another org, 403 for one the caller may not handle.
    /// </summary>
    [HttpPost("drafts")]
    [ProducesResponseType(typeof(LeaseDraftCreated), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateLeaseDraftDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizePropertyAsync(dto.PropertyId, cancellationToken) is { } denied)
            return denied;

        var created = await drafts.CreateAsync(dto.PropertyId, dto.Origin ?? LeaseOrigin.Created, cancellationToken);
        NoStore();
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>
    /// Every value of the draft, not masked, with its <c>version</c>, <c>origin</c>, <c>wizardMaxStep</c>, <c>reviewConfirmedAt</c>,
    /// <c>sentForSignatureAt</c> and the <c>flow</c> (what is missing in each of the eight steps). Only a lease that is a draft: 404
    /// <c>lease_not_draft</c> for any other (its masked detail is <c>GET api/leases/{id}</c>).
    /// </summary>
    [HttpGet("{id:guid}/draft")]
    [ProducesResponseType(typeof(LeaseDraftView), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        var draft = await drafts.GetAsync(id, cancellationToken);
        NoStore();
        return Ok(draft);
    }

    /// <summary>
    /// Saves the <c>values</c> of one <c>step</c> (1 to 5), whatever they are: a value that is wrong or incomplete is kept and
    /// reported in <c>flow.steps[n].needs</c>, never a 4xx. The values are merged into the draft: a key that is there replaces the
    /// field (<c>null</c> clears it, a list is replaced), a key that is not there is left as it was. 200 with the new <c>version</c>,
    /// the <c>flow</c>, <c>updatedAt</c> and the ids of the entries of the parties. 409 <c>lease_draft_conflict</c> when
    /// <c>version</c> is not the current one (another tab saved first: nothing is written), 409 <c>lease_not_draft</c> when the lease
    /// is no longer a draft, 400 when the values are not shaped like the step's, 422 <c>lease_parties_too_many</c>. Changing a value of
    /// the first five steps after the review was confirmed withdraws the confirmation.
    /// </summary>
    [HttpPatch("{id:guid}/draft")]
    [RequestSizeLimit(SaveRequestLimit)]
    [ProducesResponseType(typeof(LeaseDraftSaved), StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(Guid id, [FromBody] SaveLeaseDraftDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        // A property chosen at step 1 is authorized as the lease's own was: the caller must be allowed to put a contract on it.
        if (dto.Step == 1 && TryReadPropertyId(dto.Values!.Value, out var propertyId)
            && await AuthorizePropertyAsync(propertyId, cancellationToken) is { } propertyDenied)
        {
            return propertyDenied;
        }

        try
        {
            var saved = await drafts.SaveAsync(id, dto.Version!.Value, dto.Step!.Value, dto.Values!.Value, cancellationToken);
            NoStore();
            return Ok(saved);
        }
        catch (LeaseDraftValuesException)
        {
            // The message names the field and never what was sent.
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "LeaseDraftValuesInvalid");
        }
    }

    /// <summary>
    /// The review of the draft: <c>checks</c> (<c>code</c>, <c>severity</c> Block · Warn · Info · Ok, <c>step</c>, <c>params</c>),
    /// worst first. Blocks are what the flow says is missing; warnings are a start date already past, another contract of the
    /// property in the same dates (one lets by rooms), a property let for short stays; to know: a non-EU tenant (Questura), a
    /// template not approved yet. No personal data in the parameters.
    /// </summary>
    [HttpGet("{id:guid}/review")]
    [ProducesResponseType(typeof(LeaseReviewView), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReview(Guid id, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Ok(await drafts.GetReviewAsync(id, cancellationToken));
    }

    /// <summary>
    /// Confirms the review: saves the moment and the impression of the values of the first five steps. 422 <c>lease_review_blocked</c>
    /// while a check blocks. The body may be omitted; with <c>version</c> it is refused (409) if the draft moved on.
    /// </summary>
    [HttpPost("{id:guid}/review/confirm")]
    [ProducesResponseType(typeof(LeaseDraftReviewResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmReview(Guid id, [FromBody] LeaseDraftActionDto? body, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Ok(await drafts.ConfirmReviewAsync(id, body?.Version, cancellationToken));
    }

    /// <summary>Withdraws the confirmation of the review (and "downloaded for signature", which rested on it).</summary>
    [HttpDelete("{id:guid}/review/confirm")]
    [ProducesResponseType(typeof(LeaseDraftReviewResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReopenReview(Guid id, [FromBody] LeaseDraftActionDto? body, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Ok(await drafts.ReopenReviewAsync(id, body?.Version, cancellationToken));
    }

    /// <summary>
    /// Marks the contract as downloaded for the signature on paper: it needs the confirmed review and passes the same gate as
    /// <c>GET contract.pdf</c>, so the state never says something that download would refuse: 422
    /// <c>contract_template_not_approved</c> while the template is not approved (nothing changes), 422 <c>contract_data_missing</c>,
    /// 422 <c>lease_review_not_confirmed</c>. It does not change the status of the lease: the signature stays the offline one.
    /// </summary>
    [HttpPost("{id:guid}/sent")]
    [ProducesResponseType(typeof(LeaseDraftSentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkSent(Guid id, [FromBody] LeaseDraftActionDto? body, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Ok(await drafts.MarkSentAsync(id, body?.Version, cancellationToken));
    }

    /// <summary>Takes back "downloaded for signature".</summary>
    [HttpDelete("{id:guid}/sent")]
    [ProducesResponseType(typeof(LeaseDraftSentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeSent(Guid id, [FromBody] LeaseDraftActionDto? body, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Ok(await drafts.RevokeSentAsync(id, body?.Version, cancellationToken));
    }

    /// <summary>
    /// Deletes a draft with its parties, signers, events and every file tied to it: 204. 409 <c>lease_not_deletable</c> for a lease
    /// that is not a draft, has a signed copy or a rent plan. With the only draft of a property gone, the property can change mode
    /// again (<c>property_mode_blocked_by_draft_lease</c>, BE-PM02-1).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        await drafts.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Imports a contract already signed outside CasaZen (LR-13, decisions LR-D2 and LR-D28): a PDF (checked on its content, at most
    /// 20 MB, private bucket) and the day it was signed (not after today). The only way to a Signed lease without a contract template of
    /// ours: <c>signed-document</c> and <c>sent</c> of a contract made in CasaZen go on answering 422
    /// <c>contract_template_not_approved</c> until a template is approved. Only a draft created with <c>origin: Imported</c> whose
    /// review has nothing blocking (steps 1 to 4 complete; no APE needed, no range of a concordato checked); the lease keeps its
    /// <c>id</c>. Needs <c>lease.create</c> and <c>lease.sign</c> on the property of the lease: it records a signature. The signed PDF
    /// is also the <c>Contract</c> document of the lease (<c>GET api/leases/{id}/documents</c>). The registration, if the contract is
    /// already registered, is declared afterwards with <c>POST api/leases/{id}/registration/manual</c>. 409 <c>lease_not_draft</c>
    /// (also for a second import), 409 <c>lease_draft_conflict</c> (the lease is busy: nothing was written), 422
    /// <c>lease_import_not_imported_origin</c>, <c>lease_review_blocked</c>, <c>lease_signed_contract_invalid</c>,
    /// <c>lease_stipula_date_in_future</c>. Multipart fields: <c>stipulaDate</c>, <c>signedContract</c>. Answers the masked detail of
    /// the lease, as <c>signed-document</c> does.
    /// </summary>
    [HttpPost("{id:guid}/import")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.LeaseSign)]
    [RequestSizeLimit(ImportRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImportRequestLimit)]
    [ProducesResponseType(typeof(LeaseDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(
        Guid id,
        [FromForm] SignedDocumentForm form,
        [FromServices] ILeaseSigningService signing,
        [FromServices] ILeaseWorkflowService leases,
        [FromServices] TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();
        if (await AuthorizeLeaseToSignAsync(id, cancellationToken) is { } denied)
            return denied;

        await using (var signedContract = form.SignedContract!.OpenReadStream())
        {
            await signing.ImportSignedContractAsync(
                id,
                userId,
                new ImportedContractDeclaration(form.StipulaDate!.Value, signedContract, form.SignedContract.Length, form.SignedContract.FileName),
                cancellationToken);
        }

        return Ok(LeaseDtoMapper.ToDetail((await leases.GetLeaseDetailAsync(id))!, clock.TodayInRome()));
    }

    // ---- authorization -------------------------------------------------------------------------------------------

    /// <summary>
    /// Authorizes <c>lease.create</c> <b>and</b> <c>lease.sign</c> on the property of the lease as a row of that property (the import
    /// records a signature, so who may only create drafts cannot use it to sign): <c>null</c> when both are allowed, otherwise the
    /// answer (404 <c>lease_not_found</c> for a lease of another org or none, 403).
    /// </summary>
    private async Task<IActionResult?> AuthorizeLeaseToSignAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (await drafts.FindPropertyIdAsync(leaseId, cancellationToken) is not { } propertyId)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        return await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create)
            && await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Sign)
                ? null
                : Forbid();
    }

    /// <summary>
    /// Authorizes <c>lease.create</c> on the property of the lease as a row of that property: <c>null</c> when allowed, otherwise
    /// the answer (404 <c>lease_not_found</c> for a lease of another org or none, 403 for a property the caller may not handle).
    /// </summary>
    private async Task<IActionResult?> AuthorizeLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (await drafts.FindPropertyIdAsync(leaseId, cancellationToken) is not { } propertyId)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        return await AuthorizePropertyAsync(propertyId, cancellationToken, LeaseNotFoundCode, "LeaseNotFound");
    }

    private async Task<IActionResult?> AuthorizePropertyAsync(
        Guid propertyId,
        CancellationToken cancellationToken,
        string notFoundCode = PropertyNotFoundCode,
        string notFoundKey = "PropertyNotFound")
    {
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, notFoundCode, notFoundKey);

        return await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create) ? null : Forbid();
    }

    /// <summary>The <c>propertyId</c> of the values of step 1, when it is an id (anything else is refused by the service).</summary>
    private static bool TryReadPropertyId(JsonElement values, out Guid propertyId)
    {
        propertyId = Guid.Empty;
        if (values.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in values.EnumerateObject())
        {
            if (string.Equals(property.Name, "propertyId", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && Guid.TryParse(property.Value.GetString(), out propertyId))
            {
                return true;
            }
        }

        return false;
    }

    private void NoStore() => Response.Headers.CacheControl = "private, no-store";
}

/// <summary>A new draft: the property and, optionally, how the lease came to be (<c>Created</c> by default).</summary>
public sealed record CreateLeaseDraftDto([param: Required] Guid PropertyId, LeaseOrigin? Origin = null);

/// <summary>
/// One save of the draft: the <c>version</c> it was made on, the <c>step</c> (1 to 5) and its <c>values</c>. The values are an object
/// whose keys are the fields of that step: <c>propertyId</c> (1), <c>landlords</c> and <c>tenants</c> (2, lists of
/// <c>id?, firstName, lastName, fiscalCode, citizenship, residence, contactEmail</c>), <c>contractType</c>, <c>taxRegime</c>,
/// <c>monthlyRent</c>, <c>securityDeposit</c> and <c>concordato</c> (3), <c>startDate</c> (4), <c>parties</c> (5, a list of
/// <c>id, documentsVerifiedOn</c>).
/// </summary>
public sealed record SaveLeaseDraftDto(
    [param: Required] int? Version,
    [param: Required, Range(1, 5)] int? Step,
    [param: Required] JsonElement? Values);

/// <summary>Optional body of the actions on a draft: the version the caller saw. If it is not the current one the action is refused (409), not applied.</summary>
public sealed record LeaseDraftActionDto(int? Version = null);
