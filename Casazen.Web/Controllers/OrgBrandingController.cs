using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public-site branding of the caller's org (BK-12, A3-17): logo, hero image, primary color, tagline and theme, shown
/// on <c>/book/{slug}</c> through <c>GET /api/public/orgs/{slug}</c> and <c>GET /api/public/resolve-host</c>. Same
/// policy as the org settings (PL-04): only the org's billing/settings administrator.
/// </summary>
[ApiController]
[Route("api/orgs/me/branding")]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public class OrgBrandingController(
    IOrgContextResolver orgContextResolver,
    IOrgService orgService,
    IOrgBrandingService brandingService,
    IEntitlementService entitlementService,
    IPublicHostResolver publicHostResolver,
    IOptions<PublicHostOptions> publicHostOptions) : ControllerBase
{
    /// <summary>Multipart body limit of the image uploads: the largest image (hero) plus the form overhead.</summary>
    private const long MaxUploadRequestBytes = 11 * 1024 * 1024;

    [HttpGet]
    [ProducesResponseType(typeof(OrgBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgBrandingDto>> Get(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await orgService.GetByIdAsync(orgId.Value, cancellationToken);
        return org is null ? OrgNotFound() : Ok(ToDto(org));
    }

    /// <summary>
    /// Replaces primary color, theme and tagline. 422 <c>org_branding_color_invalid</c>,
    /// <c>org_branding_theme_invalid</c> or <c>org_branding_tagline_too_long</c> for an invalid value (nothing saved).
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(OrgBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrgBrandingDto>> Update(
        [FromBody] UpdateOrgBrandingDto dto,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await brandingService.UpdateAsync(
            orgId.Value,
            new OrgBrandingUpdate(dto.PrimaryColor, dto.PublicThemeId, dto.Tagline),
            cancellationToken);
        return org is null ? OrgNotFound() : Saved(org);
    }

    /// <summary>
    /// Uploads the logo (multipart field <c>file</c>): PNG, JPEG or WebP, at most 2 MB, 64×32 to 4000×4000 px.
    /// 422 <c>org_branding_image_*</c> otherwise. The previous logo is deleted from the storage.
    /// </summary>
    [HttpPut("logo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
    [ProducesResponseType(typeof(OrgBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<OrgBrandingDto>> UploadLogo(IFormFile? file, CancellationToken cancellationToken) =>
        UploadAsync(BrandingImageKind.Logo, file, cancellationToken);

    /// <summary>Removes the logo: the site shows the display name again.</summary>
    [HttpDelete("logo")]
    [ProducesResponseType(typeof(OrgBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<OrgBrandingDto>> RemoveLogo(CancellationToken cancellationToken) =>
        RemoveAsync(BrandingImageKind.Logo, cancellationToken);

    /// <summary>
    /// Uploads the hero image (multipart field <c>file</c>): PNG, JPEG or WebP, at most 10 MB, 1200×400 to 8000×8000 px.
    /// 422 <c>org_branding_image_*</c> otherwise. The previous hero image is deleted from the storage.
    /// </summary>
    [HttpPut("hero")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
    [ProducesResponseType(typeof(OrgBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<OrgBrandingDto>> UploadHero(IFormFile? file, CancellationToken cancellationToken) =>
        UploadAsync(BrandingImageKind.Hero, file, cancellationToken);

    /// <summary>Removes the hero image: the landing falls back to the first property photo.</summary>
    [HttpDelete("hero")]
    [ProducesResponseType(typeof(OrgBrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<OrgBrandingDto>> RemoveHero(CancellationToken cancellationToken) =>
        RemoveAsync(BrandingImageKind.Hero, cancellationToken);

    private async Task<ActionResult<OrgBrandingDto>> UploadAsync(
        BrandingImageKind kind,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        if (file is null || file.Length == 0)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OrgBrandingRules.ImageEmptyCode, "OrgBrandingImageEmpty");

        await using var content = file.OpenReadStream();
        var org = await brandingService.SetImageAsync(orgId.Value, kind, content, cancellationToken);
        return org is null ? OrgNotFound() : Saved(org);
    }

    private async Task<ActionResult<OrgBrandingDto>> RemoveAsync(BrandingImageKind kind, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var org = await brandingService.RemoveImageAsync(orgId.Value, kind, cancellationToken);
        return org is null ? OrgNotFound() : Saved(org);
    }

    /// <summary>The resolve-host cache carries the branding: drop it so subdomain/custom-domain sites show the change.</summary>
    private OkObjectResult Saved(Org org)
    {
        publicHostResolver.InvalidateOrgHosts(org, publicHostOptions.Value.NormalizedBaseDomain);
        return Ok(ToDto(org));
    }

    private OrgBrandingDto ToDto(Org org) => OrgBrandingDto.FromOrg(org, entitlementService.ResolveEffectiveTier(org));

    private ObjectResult OrgNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");
}
