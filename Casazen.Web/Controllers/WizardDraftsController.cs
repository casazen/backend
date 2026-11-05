using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Wizards;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The draft a person has of a wizard (ADR-024, E01/F02/US01): read it to resume on any device, save it at every step and blur, discard it
/// when the wizard is done. <c>{type}</c> is the name of a <see cref="WizardType"/> (<c>ListingCreate</c>, <c>BookingCreate</c>...); the
/// draft of a wizard that edits something is told by <c>entityId</c> (the query of <c>GET</c> and <c>DELETE</c>, a member of the body of
/// <c>POST</c>), none meaning the draft of a new entity.
/// </summary>
/// <remarks>
/// <para>TN-3. The draft is the caller's own, so the policy is <see cref="CasazenPolicies.Authenticated"/> (every context has wizards) and
/// the person is always the token's <c>sub</c>, never a parameter; the org is the caller's own (<see cref="IOrgContextResolver"/>). Which
/// draft the caller reaches is decided in one place, <see cref="IWizardDraftService"/>: the one of that person in that org. The draft of
/// another person, or of another org, is a 404, never a 403: the same answer as a draft that does not exist.</para>
/// <para>Conflict. A save carries the <c>clientVersion</c> the client last saw. A client behind the server (another device saved first)
/// gets 409 <c>draft_conflict</c> and nothing is written; the problem carries <c>serverDraft</c> (the draft as it is on the server, to
/// offer "keep mine" or "take theirs") and <c>serverVersion</c>.</para>
/// <para>The answers hold what the person typed, personal data included, in clear to their owner only: they are private and never cached
/// (<c>Cache-Control: private, no-store</c>), and nothing of the content is logged.</para>
/// </remarks>
[ApiController]
[Route("api/wizards/{type}/draft")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class WizardDraftsController(IWizardDraftService drafts, IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// Bytes a save may send: twice the data a draft may hold (<see cref="WizardDraftLimits.MaxDataBytes"/>), for the members around it
    /// and for a client that escapes its text. A draft over the limit is refused by the service with 413 <c>wizard_draft_too_large</c>.
    /// </summary>
    public const int MaxBodyBytes = 2 * WizardDraftLimits.MaxDataBytes;

    /// <summary>
    /// The caller's draft of the wizard, with the typed data put together again. <c>entityId</c>: what the wizard edits; none, the draft of
    /// a new entity.
    /// </summary>
    /// <response code="200"><c>{ type, entityId, version, data, lastSavedAt }</c>.</response>
    /// <response code="400"><c>wizard_type_unknown</c>.</response>
    /// <response code="404"><c>wizard_draft_not_found</c>: none saved, it expired, or it is another person's (the same answer); or the caller has no org.</response>
    [HttpGet]
    [ProducesResponseType(typeof(WizardDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WizardDraftResponse>> Get(
        string type,
        [FromQuery] Guid? entityId,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();
        if (!EnumNames.TryParseDefined<WizardType>(type, out var wizardType))
            return UnknownType();
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
            return NoOrganization();

        var draft = await drafts.GetDraftAsync(orgId, userId, wizardType, entityId, cancellationToken);
        if (draft is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, WizardDraftErrors.NotFound, "WizardDraftNotFound");

        Response.RegisterForDispose(draft.Data);
        MarkPrivate();
        return Ok(WizardDraftResponse.From(draft));
    }

    /// <summary>
    /// Saves the caller's draft of the wizard (the first save creates it). Body: <c>{ entityId?, clientVersion, data }</c>, with
    /// <c>clientVersion</c> the <c>version</c> the client last saw (0 at the first save) and <c>data</c> a JSON object; a <c>type</c> in the
    /// body, if there, is the same as the address. The fields that name a person are stored encrypted, the rest in clear.
    /// </summary>
    /// <response code="200">The draft as saved: <c>{ type, entityId, version, data, lastSavedAt }</c>; <c>lastSavedAt</c> is the "saved at" of the screen.</response>
    /// <response code="400"><c>wizard_type_unknown</c>, <c>wizard_type_mismatch</c>, <c>wizard_draft_data_invalid</c>, or a field error.</response>
    /// <response code="409"><c>draft_conflict</c>: the client is behind the server, or lost a parallel save; nothing was written. The problem carries <c>serverDraft</c> and <c>serverVersion</c>.</response>
    /// <response code="413"><c>wizard_draft_too_large</c>.</response>
    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(WizardDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<WizardDraftResponse>> Save(
        string type,
        [FromBody] SaveWizardDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();
        if (!EnumNames.TryParseDefined<WizardType>(type, out var wizardType))
            return UnknownType();
        if (request.Type is { } bodyType && bodyType != wizardType)
            return this.ApiProblem(StatusCodes.Status400BadRequest, WizardDraftErrors.TypeMismatch, "WizardDraftTypeMismatch");
        if (request.Data is not { } data)
            return DataInvalid();

        // The document of the body lives as long as the request.
        Response.RegisterForDispose(data);
        if (data.RootElement.ValueKind != JsonValueKind.Object || WizardDraftPii.HasNullCharacter(data.RootElement))
            return DataInvalid();
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
            return NoOrganization();

        var result = await drafts.SaveDraftAsync(
            orgId,
            userId,
            new SaveDraftRequest(wizardType, request.EntityId, request.ClientVersion, data),
            cancellationToken);

        MarkPrivate();
        if (result.ServerDraft is { } serverDraft)
            Response.RegisterForDispose(serverDraft.Data);

        return result.Status == DraftStatus.Conflict
            ? DraftConflict(result)
            : Ok(WizardDraftResponse.From(result.ServerDraft!));
    }

    /// <summary>
    /// Discards the caller's draft of the wizard (the wizard is done, or the person gave it up). Idempotent: no draft is not an error.
    /// Never reaches the draft of another person.
    /// </summary>
    /// <response code="204">Discarded (or there was none).</response>
    /// <response code="400"><c>wizard_type_unknown</c>.</response>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        string type,
        [FromQuery] Guid? entityId,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();
        if (!EnumNames.TryParseDefined<WizardType>(type, out var wizardType))
            return UnknownType();
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
            return NoOrganization();

        await drafts.DeleteDraftAsync(orgId, userId, wizardType, entityId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The 409 <c>draft_conflict</c> through the API error contract, with the draft of the server and its version as members of the
    /// problem (<see cref="WizardDraftConflictResponse"/>), as <c>lease_period_overlaps</c> carries the lease in the way.
    /// </summary>
    private ObjectResult DraftConflict(WizardDraftResult result)
    {
        var problem = this.ApiProblem(StatusCodes.Status409Conflict, WizardDraftErrors.Conflict, "WizardDraftConflict");
        if (problem.Value is ProblemDetails details)
        {
            var conflict = new WizardDraftConflictResponse(
                result.ServerDraft is { } server ? WizardDraftResponse.From(server) : null,
                result.ServerVersion);
            details.Extensions["serverDraft"] = conflict.ServerDraft;
            details.Extensions["serverVersion"] = conflict.ServerVersion;
        }

        return problem;
    }

    private ObjectResult UnknownType() =>
        this.ApiProblem(StatusCodes.Status400BadRequest, WizardDraftErrors.TypeUnknown, "WizardDraftTypeUnknown");

    private ObjectResult DataInvalid() =>
        this.ApiProblem(StatusCodes.Status400BadRequest, WizardDraftErrors.DataInvalid, "WizardDraftDataInvalid");

    /// <summary>A caller with no host org (a supplier-only account, or before the onboarding) has no drafts.</summary>
    private ObjectResult NoOrganization() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

    private void MarkPrivate() => Response.Headers.CacheControl = "private, no-store";
}
