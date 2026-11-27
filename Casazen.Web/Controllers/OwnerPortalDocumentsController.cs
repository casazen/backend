using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/owner-portal/external-documents")]
[Authorize(Policy = CasazenPolicies.OwnerPortal)]
public class OwnerPortalDocumentsController(
    IOwnerAccountingService accounting,
    IOptions<Auth0Options> auth0Options,
    ILogger<OwnerPortalDocumentsController> logger) : ControllerBase
{
    private readonly string? _customClaimNamespace = auth0Options.Value.CustomClaimNamespace;

    [HttpGet]
    [ProducesResponseType(typeof(List<OwnerPortalExternalDocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<OwnerPortalExternalDocumentDto>>> List()
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId is null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var documents = await accounting.ListPublishedExternalDocumentsForOwnerUserAsync(ownerUserId.Value, HttpContext.RequestAborted);
        return Ok(documents.Select(OwnerPortalExternalDocumentDto.From).ToList());
    }
}

public sealed record OwnerPortalExternalDocumentDto(
    Guid Id,
    Guid StatementVersionId,
    string Provider,
    string ExternalNumber,
    DateOnly ExternalDate,
    string? DocumentUrl,
    string? DocumentSha256,
    OwnerExternalDocumentStatus Status)
{
    public static OwnerPortalExternalDocumentDto From(OwnerExternalDocumentReference r) =>
        new(r.Id, r.OwnerStatementVersionId, r.Provider, r.ExternalNumber, r.ExternalDate, r.DocumentUrl, r.DocumentSha256, r.Status);
}
