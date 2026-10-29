using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The content of the supplier's showcase (SP-13a): the draft the editor autosaves, and what is published. Every route is scoped to
/// the caller's own supplier org (its supplier link, never a value of the request), so a supplier never reaches the content of another
/// one. Not behind a feature flag: the content changes nothing that customers see until the supplier publishes it. Runbook
/// <c>docs/runbooks/suppliers.md</c> section 33.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no linked supplier org; 400 <c>validation_error</c> for a malformed or oversized body;
/// 422 <c>showcase_content_invalid</c> (with <c>fields</c>: the paths at fault, and <c>suggestedColor</c> when the color cannot be read with
/// white text); 409 <c>showcase_version_conflict</c> when the content was changed since the client read it.
/// </remarks>
[ApiController]
[Route("api/supplier/showcase")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierShowcaseContentController(
    ISupplierShowcaseContentService content,
    ISupplierService supplierService,
    ISupplierOrgContextResolver supplierOrgContextResolver,
    PublicSiteLinks publicSiteLinks) : ControllerBase
{
    /// <summary>
    /// The content: the draft (empty before the first save), what is published, the state (<c>Draft</c>, <c>Online</c>, <c>ChangesReady</c>,
    /// <c>Paused</c>), what a publication would change (<c>changes</c>), the <c>version</c> to send back with the next save and the public
    /// address. A read creates no row.
    /// </summary>
    [HttpGet("content")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShowcaseContentResponse>> Get(CancellationToken cancellationToken) =>
        await RunAsync(orgId => content.GetAsync(orgId, cancellationToken), ensureAddress: true, cancellationToken);

    /// <summary>
    /// Saves a change of the draft (the editor's autosave). The body is the <c>version</c> the editor read and the members to change, as a
    /// JSON merge patch: a member that is there replaces the stored one, an object is merged member by member, a list replaces the whole
    /// list, <c>null</c> clears. Texts are cleaned and every field is checked on the whole result (limits, the color, the images, the
    /// services); a draft may be incomplete. 409 <c>showcase_version_conflict</c> for a stale version (reload and apply the change again),
    /// 422 <c>showcase_content_invalid</c> for a value that does not fit the contract.
    /// </summary>
    [HttpPatch("content")]
    [EnableRateLimiting(RateLimitPolicies.SupplierShowcaseSave)]
    [RequestSizeLimit(ShowcaseContentLimits.MaxBodyBytes)]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ShowcaseContentResponse>> Save(
        [FromBody] SaveShowcaseContentRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            orgId => content.SaveDraftAsync(orgId, request.Version!.Value, request.ToPatch(), cancellationToken),
            ensureAddress: false,
            cancellationToken);

    /// <summary>
    /// Throws the changes of the draft away: it goes back to what is published (an empty draft while nothing is). The body may carry the
    /// <c>version</c> the editor read; when it does it must be the current one (409 <c>showcase_version_conflict</c>).
    /// </summary>
    [HttpPost("discard")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShowcaseContentResponse>> Discard(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ShowcaseCommandRequest? request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            orgId => content.DiscardAsync(orgId, request?.Version, cancellationToken),
            ensureAddress: false,
            cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> for the caller's linked supplier org. The org comes only from the caller's own supplier link
    /// (<see cref="ISupplierOrgContextResolver.GetLinkedSupplierOrgIdAsync"/>): the content never provisions a supplier org, it is business
    /// data. The answer carries the public address; <paramref name="ensureAddress"/> gives an active supplier that has none yet (activated
    /// before SU-13) its slug first, as the preview of the showcase does.
    /// </summary>
    private async Task<ActionResult<ShowcaseContentResponse>> RunAsync(
        Func<Guid, Task<ShowcaseContentView>> action,
        bool ensureAddress,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var view = await action(orgId.Value);

        if (ensureAddress)
            await supplierService.EnsureShowcaseSlugAsync(orgId.Value, cancellationToken);
        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        var path = string.IsNullOrEmpty(profile?.ShowcaseSlug) ? null : PublicSitePaths.SupplierShowcase(profile.ShowcaseSlug);
        var url = path is null ? null : publicSiteLinks.TryPublicPage(path);

        return Ok(ShowcaseContentMapper.ToResponse(view, path, url));
    }
}
