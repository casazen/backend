using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The images of the supplier's showcase (SP-13b): the logo, the cover and the photos of the jobs, uploaded from the editor. Every route is
/// scoped to the caller's own supplier org (its supplier link, never a value of the request), so a supplier never reaches the images of another
/// one. Not behind a feature flag, like the content (<see cref="SupplierShowcaseContentController"/>): a file changes nothing that customers see until
/// the supplier puts it in the content and publishes it. Runbook <c>docs/runbooks/suppliers.md</c> section 33b.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no linked supplier org; 413 <c>showcase_media_too_large</c> (a logo over 200 KB, a photo over
/// 10 MB, a request over any file); 415 <c>showcase_media_unsupported_type</c> (not a PNG, a JPEG or a WebP by its content); 422
/// <c>showcase_media_none</c>, <c>_kind_invalid</c>, <c>_invalid_image</c>, <c>_dimensions</c> or <c>_limit_reached</c>; 429 from the rate limit.
/// </remarks>
[ApiController]
[Route("api/supplier/showcase/media")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierShowcaseMediaController(
    ISupplierShowcaseMediaService media,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// Uploads an image of the showcase (multipart: <c>kind</c>, <c>logo</c>, <c>cover</c>, <c>work</c> or <c>before</c>, and <c>file</c>). The type
    /// is read from the content (PNG, JPEG or WebP; an SVG is refused), the file is stored <b>without its metadata</b> (EXIF with the GPS position,
    /// XMP, comments) under a name nobody can guess, and the answer carries the address to put into the content. A logo is 200 KB at most, a photo
    /// 10 MB. All or nothing.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(RateLimitPolicies.SupplierShowcaseMedia)]
    [RequestSizeLimit(ShowcaseMediaLimits.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ShowcaseMediaLimits.MaxRequestBytes)]
    [ProducesResponseType(typeof(ShowcaseMediaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ShowcaseMediaDto>> Upload([FromForm] ShowcaseMediaForm form, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var item = await media.UploadAsync(orgId.Value, form.Kind, form.File, cancellationToken);
        return Ok(ShowcaseMediaDto.From(item));
    }

    /// <summary>
    /// Removes an image of the supplier when nothing refers to it: not the draft, not the published version, not the one before it (409
    /// <c>showcase_media_in_use</c>: take it out of the content first). The files nobody uses are also removed by themselves each night.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitPolicies.SupplierShowcaseMedia)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        await media.DeleteAsync(orgId.Value, id, cancellationToken);
        return NoContent();
    }
}
