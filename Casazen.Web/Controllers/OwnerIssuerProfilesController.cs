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
/// The issuer profile of an owner (NF-116, D12, E17/F01/US01/T01): the data with which CasaZen issues the documents of the owner's stays in the owner's name
/// (the receipt of a private owner, the FatturaPA of a company), with the owner's own series (the sezionale). Reading needs <c>agency.owner.view</c> and an owner
/// in the caller's reach; saving needs <c>agency.owner.financial.manage</c> and the whole organization (403 <c>fiscal_settings_need_whole_org</c> for a
/// collaborator «Solo alcuni»: how the stays of an owner are invoiced is the agency's decision). Behind <c>Features:FiscalDocumentIssuance</c>: 404 while it is off.
/// </summary>
/// <remarks>
/// The personal data of the profile are in clear only for a caller who may change it (the whole organization and <c>agency.owner.financial.manage</c>); such a
/// read is logged with ids, never the values. Any other caller gets them masked. An owner of another org, or out of the caller's reach, is 404 <c>owner_not_found</c>.
/// </remarks>
[ApiController]
[Route("api/owners/{ownerId:guid}/issuer-profile")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
[FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
public class OwnerIssuerProfilesController(
    IOwnerIssuerProfileService profiles,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService,
    ILogger<OwnerIssuerProfilesController> logger) : ControllerBase
{
    /// <summary>
    /// The profile of the owner, or the empty one of an owner that has none (<c>exists: false</c>): the data, the regime, the series, the fields still missing
    /// (<c>missingFields</c>) and the tax rule in force today. 404 <c>owner_not_found</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(OwnerIssuerProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerIssuerProfileDto>> Get(Guid ownerId)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        var view = await profiles.GetAsync(scope, ownerId, HttpContext.RequestAborted);
        var full = await MayChangeAsync(scope);
        if (full && view.ProfileId is not null)
            logger.LogInformation("User {UserId} read the issuer profile of owner {OwnerId} in full", User.GetUserId(), ownerId);

        Response.Headers.CacheControl = "private, no-store";
        return Ok(OwnerIssuerProfileDto.From(view, full));
    }

    /// <summary>
    /// Saves the profile of the owner as a whole (made at the first save). An incomplete profile is saved and says what is missing; nothing is issued on it until
    /// it is complete. 403 <c>fiscal_settings_need_whole_org</c>; 404 <c>owner_not_found</c>; 422 <c>issuer_field_invalid</c> (with the <c>field</c> and the
    /// <c>errors</c>), <c>issuer_regime_country_mismatch</c>, <c>document_issuance_not_available</c>. A refusal saves nothing.
    /// </summary>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerIssuerProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerIssuerProfileDto>> Save(Guid ownerId, [FromBody] SaveOwnerIssuerProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        // How the stays of an owner are invoiced is the agency's decision: a caller who reaches only some of its properties cannot make it.
        if (!scope.IsOrgWide)
        {
            logger.LogWarning("User {UserId} may not change the issuer profile of owner {OwnerId}: its scope is not the whole org", User.GetUserId(), ownerId);
            return this.ApiProblem(
                StatusCodes.Status403Forbidden,
                DocumentIssuerErrorCodes.NeedsWholeOrg,
                DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.NeedsWholeOrg));
        }

        var saved = await profiles.SaveAsync(scope.OrgId, ownerId, request.ToCommand(), User.GetUserId(), HttpContext.RequestAborted);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(OwnerIssuerProfileDto.From(saved, full: true));
    }

    /// <summary>
    /// The export for the accountant of the owner (E17/F02/US01/T01, NF-116, acceptance steps C8 and D2): <c>GET api/owners/{ownerId}/issued-documents/export?period=2026-10</c> (or
    /// <c>?period=2026</c>), one ZIP, <c>casazen-documenti-emessi-2026-10.zip</c>, streamed: the PDF and the XML of the documents CasaZen issued in the name of the owner in the period, their
    /// register (CSV and Excel: the documents, the VAT of each, the totals for each rate and nature, the stamp duty) and <c>indice.csv</c> with the SHA-256 of every file; for a year also the
    /// annual summary of those documents and the invoices of the agency to the owner with their recharges. <c>agency.statement.view</c> on top of the class, and the whole organization (403
    /// otherwise, before anything is read): the accounts of an owner span the properties of the owner, so a caller who reaches only some of them reads none of it. 400
    /// <c>accountant_package_invalid_period</c>; 404 <c>owner_not_found</c> for an owner of another org; 422 <c>accountant_package_too_large</c> before the first byte. The owner reads the same
    /// ZIP in its portal (<c>GET api/owner-portal/issued-documents/export</c>).
    /// </summary>
    [HttpGet("~/api/owners/{ownerId:guid}/issued-documents/export")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementView)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportIssuedDocuments(
        Guid ownerId,
        [FromQuery] string? period,
        [FromServices] IOwnerIssuedDocumentsExport issuedDocuments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issuedDocuments);
        if (!AccountantPackagePeriod.TryParse(period, out var parsed))
            return this.ApiProblem(StatusCodes.Status400BadRequest, AccountantPackageErrors.InvalidPeriod, "AccountantPackageInvalidPeriod");

        if (await ScopeAsync() is not { IsOrgWide: true } scope)
        {
            logger.LogWarning("User {UserId} may not export the documents issued in the name of owner {OwnerId}: its scope is not the whole org", User.GetUserId(), ownerId);
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
        }

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        ZipDownloadHeaders.WhenTheResponseStarts(Response, OwnerIssuedDocumentsStructure.FileNameOf(parsed));
        var result = await issuedDocuments.BuildAsync([new OwnerIssuedDocumentsOwner(scope.OrgId, ownerId)], parsed, Response.Body, cancellationToken);

        logger.LogInformation(
            "Owner issued documents exported. OrgId={OrgId} OwnerId={OwnerId} UserId={UserId} Documents={Documents} Files={Files} Bytes={Bytes}",
            scope.OrgId,
            ownerId,
            User.GetUserId(),
            result.Documents,
            result.Files,
            result.Bytes);
        return new EmptyResult();
    }

    /// <summary>The caller's reach on its org; <c>null</c> without an org or a reach (never widened to the whole org).</summary>
    private async Task<HostScope?> ScopeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
    }

    /// <summary>The caller may change the profile, and so read it in full: the whole organization and <c>agency.owner.financial.manage</c>.</summary>
    private async Task<bool> MayChangeAsync(HostScope scope) =>
        scope.IsOrgWide && (await authorizationService.AuthorizeAsync(User, CasazenPolicies.AgencyOwnerFinancialManage)).Succeeded;
}
