using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier's catalog of services with prices (SP-02): name, category, "from" price or on quote, duration,
/// supplements, what is included and excluded, photos, weekdays and notice, as drafts, published or paused services.
/// Every route is scoped to the caller's own supplier org (its supplier link, never a value of the request): a service of
/// another supplier, or a deleted one, is 404 <c>supplier_service_not_found</c>. Not behind a feature flag: every supplier
/// has a catalog. Runbook <c>docs/runbooks/suppliers.md</c> section 19.
/// </summary>
/// <remarks>
/// Errors: 404 <c>supplier_service_not_found</c> (and <c>not_found</c> when the caller has no linked supplier org);
/// 400 <c>validation_error</c> for an oversized or malformed body; 422 <c>invalid_service_category</c>,
/// <c>supplier_service_invalid</c> and <c>supplier_service_not_publishable</c> (these two with <c>fields</c>: the names of
/// the fields at fault), <c>supplier_service_limit_reached</c>, <c>supplier_service_cannot_pause</c> and the photo codes;
/// 409 <c>supplier_service_changed</c> when the service was changed since the client read it.
/// </remarks>
[ApiController]
[Route("api/supplier/services")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierServicesController(
    ISupplierServiceCatalogService catalog,
    ISupplierOrgContextResolver supplierOrgContextResolver,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    /// <summary>The caller's services that are not deleted, by position then creation date, with the limit per supplier.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(SupplierServiceListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierServiceListResponse>> List(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var items = await catalog.ListAsync(orgId, cancellationToken);
                return Ok(new SupplierServiceListResponse
                {
                    Items = items.Select(SupplierServiceMapper.ToDto).ToList(),
                    Total = items.Count,
                });
            },
            cancellationToken);

    /// <summary>One service of the caller's catalog.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SupplierServiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierServiceDto>> Get(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierServiceMapper.ToDto(await catalog.GetAsync(orgId, id, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Creates a service as a <c>Draft</c> (it may be incomplete: the wizard saves a draft at every step): the slug comes
    /// from the name, the position is the last one. 201 with the service. 422 <c>supplier_service_limit_reached</c> at 30
    /// services; photos are uploaded afterwards (<c>POST {id}/photos</c>).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(SupplierServiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierServiceDto>> Create(
        [FromBody] SaveSupplierServiceRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var created = await catalog.CreateAsync(orgId, SupplierServiceMapper.ToInput(request), cancellationToken);
                var dto = SupplierServiceMapper.ToDto(created);
                return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
            },
            cancellationToken);

    /// <summary>
    /// Replaces the content of a service: every field of the body (one left out takes its default), except the photos and
    /// the position, which stay when the body does not carry them. <c>version</c> is the one the client read: another is a
    /// 409 <c>supplier_service_changed</c>. The slug and the status do not change (a draft's slug follows its name); a
    /// published service must stay publishable (422 <c>supplier_service_not_publishable</c>).
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(SupplierServiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierServiceDto>> Update(
        Guid id,
        [FromBody] UpdateSupplierServiceRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var updated = await catalog.UpdateAsync(
                    orgId, id, request.Version!.Value, SupplierServiceMapper.ToInput(request), cancellationToken);
                return Ok(SupplierServiceMapper.ToDto(updated));
            },
            cancellationToken);

    /// <summary>Deletes a service (soft: it disappears from every read and its slug is free again). 204.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                await catalog.DeleteAsync(orgId, id, cancellationToken);
                return NoContent();
            },
            cancellationToken);

    /// <summary>
    /// Publishes a draft or paused service: 422 <c>supplier_service_not_publishable</c> (with <c>fields</c>) while it lacks a
    /// name, a category, a duration, or a price or the quote flag. An already published service is returned as it is.
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType(typeof(SupplierServiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierServiceDto>> Publish(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierServiceMapper.ToDto(await catalog.PublishAsync(orgId, id, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Pauses a published service (it can be published again). A paused service is returned as it is; a draft is 422
    /// <c>supplier_service_cannot_pause</c>.
    /// </summary>
    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(SupplierServiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierServiceDto>> Pause(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierServiceMapper.ToDto(await catalog.PauseAsync(orgId, id, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// A draft copy of a service (same content and photos, a new slug, "(copia)" after the name, last position). 201 with
    /// the copy; 422 <c>supplier_service_limit_reached</c> at 30 services.
    /// </summary>
    [HttpPost("{id:guid}/duplicate")]
    [ProducesResponseType(typeof(SupplierServiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierServiceDto>> Duplicate(Guid id, CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                var copy = await catalog.DuplicateAsync(
                    orgId, id, localizer["SupplierServiceCopySuffix"].Value, cancellationToken);
                var dto = SupplierServiceMapper.ToDto(copy);
                return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
            },
            cancellationToken);

    /// <summary>
    /// Adds photos to a service (multipart field <c>photos</c>, up to 6 files of 10 MB, JPEG/PNG/WebP checked on their
    /// content, 6 photos per service). All or none: one invalid file stores nothing. The new photos go after the existing
    /// ones (the first photo of a service is its cover). The answer is the service with its new <c>version</c>: use it for
    /// the next <c>PUT</c>. 413 for a request larger than 6 files of 10 MB.
    /// </summary>
    [HttpPost("{id:guid}/photos")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(SupplierServiceCatalogLimits.MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = SupplierServiceCatalogLimits.MaxUploadRequestBytes)]
    [ProducesResponseType(typeof(SupplierServiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierServiceDto>> UploadPhotos(
        Guid id,
        [FromForm] List<IFormFile> photos,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierServiceMapper.ToDto(await catalog.AddPhotosAsync(orgId, id, photos, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> for the caller's linked supplier org. The org comes only from the caller's own supplier
    /// link (<see cref="ISupplierOrgContextResolver.GetLinkedSupplierOrgIdAsync"/>): the catalog never provisions a supplier
    /// org, it is business data. A rule of the catalog that names its fields is answered with them (<c>fields</c>).
    /// </summary>
    private async Task<ActionResult> RunAsync(Func<Guid, Task<ActionResult>> action, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        try
        {
            return await action(orgId.Value);
        }
        catch (SupplierServiceRuleException ex)
        {
            var problem = ApiProblemDetails.Create(
                HttpContext, StatusCodes.Status422UnprocessableEntity, ex.Code, ex.MessageKey, ex.MessageArgs);
            problem.Extensions["fields"] = ex.Fields;
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
                ContentTypes = { ApiProblemDetails.ContentType },
            };
        }
    }
}
