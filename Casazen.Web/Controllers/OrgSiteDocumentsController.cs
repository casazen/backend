using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/orgs/me/site-documents")]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public class OrgSiteDocumentsController(
    IOrgContextResolver orgContextResolver,
    IOrgSiteDocumentService documentService,
    ILogger<OrgSiteDocumentsController> logger) : ControllerBase
{
    private const long MaxRequestBytes = 512 * 1024;

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
