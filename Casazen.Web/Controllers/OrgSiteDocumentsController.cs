using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The operator's privacy notice and booking terms of the public site (BK-14, A3-21): the host writes the text, or
/// links a document hosted elsewhere, and publishes it as a new version; the guest reads it on
/// <c>/book/{slug}/privacy</c> and <c>/book/{slug}/termini</c> through <c>GET /api/public/orgs/{slug}/documents/{kind}</c>.
/// CasaZen supplies no text. Same policy as the branding and the org settings (BK-12, PL-04): only the org's
/// billing/settings administrator. <c>{kind}</c> is <c>privacy</c> or <c>terms</c>.
/// </summary>
[ApiController]
[Route("api/orgs/me/site-documents")]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public class OrgSiteDocumentsController(
    IOrgContextResolver orgContextResolver,
    IOrgSiteDocumentService documentService,
    ILogger<OrgSiteDocumentsController> logger) : ControllerBase
{
    /// <summary>The largest text (50,000 characters, up to 4 bytes each) with the JSON overhead.</summary>
    private const long MaxRequestBytes = 512 * 1024;

    /// <summary>The state of both documents: what the public site shows and the recent versions.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<OrgSiteDocumentStateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<OrgSiteDocumentStateDto>>> GetAll(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NoOrganization();

        var states = await documentService.GetStatesAsync(orgId.Value, cancellationToken);
        return Ok(states.Select(OrgSiteDocumentStateDto.From).ToList());
    }

    /// <summary>One earlier (or current) version with its text, to read it or start the next one from it.</summary>
    [HttpGet("{kind}/versions/{version:int}")]
    [ProducesResponseType(typeof(OrgSiteDocumentVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgSiteDocumentVersionDto>> GetVersion(
        string kind,
        int version,
        CancellationToken cancellationToken)
    {
        if (!OrgSiteDocumentRules.TryParseKind(kind, out var parsedKind))
            return UnknownKind();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NoOrganization();

        var document = await documentService.GetVersionAsync(orgId.Value, parsedKind, version, cancellationToken);
        return document is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrgDocumentVersionNotFound")
            : Ok(OrgSiteDocumentVersionDto.From(document, includeContent: true));
    }

    /// <summary>
    /// Publishes a new version (shown right away). 422 <c>org_document_*</c> for an invalid text or address (nothing
    /// saved). Publishing what is already shown changes nothing.
    /// </summary>
    [HttpPut("{kind}")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType(typeof(OrgSiteDocumentStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrgSiteDocumentStateDto>> Publish(
        string kind,
        [FromBody] PublishOrgSiteDocumentDto dto,
        CancellationToken cancellationToken)
    {
        if (!OrgSiteDocumentRules.TryParseKind(kind, out var parsedKind))
            return UnknownKind();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NoOrganization();

        await documentService.PublishAsync(
            orgId.Value,
            parsedKind,
            new OrgSiteDocumentInput(dto.Source!.Value, dto.Content, dto.ExternalUrl),
            User.GetUserId(),
            cancellationToken);
        return Ok(await StateAsync(orgId.Value, parsedKind, cancellationToken));
    }

    /// <summary>
    /// Withdraws the current version: the public site says the operator has not published the document. Nothing is
    /// deleted, the versions stay in the history. Idempotent.
    /// </summary>
    [HttpDelete("{kind}")]
    [ProducesResponseType(typeof(OrgSiteDocumentStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgSiteDocumentStateDto>> Withdraw(string kind, CancellationToken cancellationToken)
    {
        if (!OrgSiteDocumentRules.TryParseKind(kind, out var parsedKind))
            return UnknownKind();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NoOrganization();

        var withdrawn = await documentService.WithdrawAsync(orgId.Value, parsedKind, cancellationToken);
        logger.LogInformation(
            "Site document {Kind} of org {OrgId} withdrawn by user {UserId}: {Withdrawn}",
            parsedKind, orgId, User.GetUserId(), withdrawn);
        return Ok(await StateAsync(orgId.Value, parsedKind, cancellationToken));
    }

    private async Task<OrgSiteDocumentStateDto> StateAsync(Guid orgId, OrgSiteDocumentKind kind, CancellationToken cancellationToken)
    {
        var states = await documentService.GetStatesAsync(orgId, cancellationToken);
        return OrgSiteDocumentStateDto.From(states.Single(s => s.Kind == kind));
    }

    private ObjectResult NoOrganization() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

    private ObjectResult UnknownKind() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrgDocumentKindUnknown");
}
