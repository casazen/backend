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
/// The content of the supplier's showcase (SP-13a): the draft the editor autosaves, what is published, and the publication with its
/// undo, the pause and the switch of the bookings. Every route is scoped to
/// the caller's own supplier org (its supplier link, never a value of the request), so a supplier never reaches the content of another
/// one. Not behind a feature flag: the content changes nothing that customers see until the supplier publishes it. Runbook
/// <c>docs/runbooks/suppliers.md</c> section 33.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no linked supplier org; 400 <c>validation_error</c> for a malformed or oversized body;
/// 422 <c>showcase_content_invalid</c> (with <c>fields</c>: the paths at fault, and <c>suggestedColor</c> when the color cannot be read with
/// white text); 409 <c>showcase_version_conflict</c> when the content was changed since the client read it; for the publication 422
/// <c>showcase_not_publishable</c> (with <c>fields</c>: what is missing), <c>showcase_supplier_not_active</c> and <c>showcase_not_published</c>,
/// and 409 <c>showcase_undo_unavailable</c>.
/// </remarks>
[ApiController]
[Route("api/supplier/showcase")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierShowcaseContentController(
    ISupplierShowcaseContentService content,
    ISupplierService supplierService,
    ISupplierOrgContextResolver supplierOrgContextResolver,
    ISupplierKpiService kpis,
    IShowcaseTextSuggestionService textSuggestions,
    PublicSiteLinks publicSiteLinks) : ControllerBase
{
    private const int StatsDays = 30;

    [HttpGet("content")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShowcaseContentResponse>> Get(CancellationToken cancellationToken) =>
        await RunAsync(orgId => content.GetAsync(orgId, cancellationToken), ensureAddress: true, cancellationToken);

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
    /// Publishes the draft. The readiness is judged again on the server (an active supplier, at least one active service shown, the zones it
    /// serves, a button color white text can be read on): 422 <c>showcase_not_publishable</c> with <c>fields</c> (the codes of what is missing),
    /// 422 <c>showcase_supplier_not_active</c>, or 422 <c>showcase_content_invalid</c> for a half-filled row. The rows left empty are dropped, the
    /// draft becomes what the customers read, and the version published before is kept for <c>publish/undo</c> (<c>undoUntil</c>). The answer
    /// says whether the bookings opened (<c>bookings.open</c>) and, if not, why (<c>bookings.blockedBy</c>): the showcase is then online in
    /// read-only mode. The body may carry the <c>version</c> the editor read; when it does it must be the current one (409).
    /// </summary>
    [HttpPost("publish")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ShowcaseContentResponse>> Publish(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ShowcaseCommandRequest? request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            orgId => content.PublishAsync(orgId, request?.Version, cancellationToken),
            ensureAddress: false,
            cancellationToken);

    [HttpPost("publish/undo")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShowcaseContentResponse>> UndoPublish(CancellationToken cancellationToken) =>
        await RunAsync(orgId => content.UndoPublishAsync(orgId, cancellationToken), ensureAddress: false, cancellationToken);

    /// <summary>
    /// Pauses the published showcase: the page tells customers it takes no bookings, and the bookings close. Idempotent. 422
    /// <c>showcase_not_published</c> while nothing is published.
    /// </summary>
    [HttpPost("pause")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ShowcaseContentResponse>> Pause(CancellationToken cancellationToken) =>
        await RunAsync(orgId => content.PauseAsync(orgId, cancellationToken), ensureAddress: false, cancellationToken);

    [HttpPost("resume")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ShowcaseContentResponse>> Resume(CancellationToken cancellationToken) =>
        await RunAsync(orgId => content.ResumeAsync(orgId, cancellationToken), ensureAddress: false, cancellationToken);

    [HttpGet("status")]
    [ProducesResponseType(typeof(ShowcaseStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShowcaseStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var view = await content.GetAsync(orgId.Value, cancellationToken);
        var stats = await kpis.GetShowcaseRequestStatsAsync(orgId.Value, StatsDays, cancellationToken);
        var (_, url) = await AddressAsync(orgId.Value, ensure: false, cancellationToken);

        return Ok(ShowcaseContentMapper.ToStatus(view, stats, url));
    }

    [HttpGet("stats")]
    [ProducesResponseType(typeof(ShowcaseStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShowcaseStatsDto>> GetStats(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var stats = await kpis.GetShowcaseRequestStatsAsync(orgId.Value, StatsDays, cancellationToken);
        return Ok(new ShowcaseStatsDto { Days = stats.Days, Requests = stats.Requests, Pending = stats.Pending });
    }

    [HttpGet("texts/suggestion/status")]
    [ProducesResponseType(typeof(ShowcaseTextSuggestionStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShowcaseTextSuggestionStatusDto>> GetTextSuggestionStatus(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        return Ok(new ShowcaseTextSuggestionStatusDto { Available = await textSuggestions.IsAvailableAsync(cancellationToken) });
    }

    /// <summary>
    /// Drafts for the sentence in large type, the introduction, "chi siamo" and the tips "prima che arriviamo", written by an AI provider from what is
    /// true of the supplier (its categories, its zones, the names of its services and what it typed in <c>answers</c>) and checked so that no
    /// guarantee, certification, insurance, number of years, time, price, rating or unmeasured adjective appears unless the supplier wrote it. The
    /// guarantee, the people, the strengths, the certifications and the questions are never drafted. The answer changes nothing: the editor shows it and
    /// the supplier applies it to the draft; nothing is public before the publication. Rate limited per user and org like every AI endpoint
    /// (429 <c>rate_limited</c>); the monthly platform budget is 422 <c>ai_budget_exhausted</c>; 422 <c>showcase_ai_unavailable</c> when no provider
    /// answers, <c>showcase_suggestion_not_enough_data</c> when the supplier has no category, zone, service or answer yet, and
    /// <c>showcase_suggestion_unusable</c> when every member was dropped by the checks.
    /// </summary>
    [HttpPost("texts/suggestion")]
    [AiRateLimit]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType(typeof(ShowcaseTextSuggestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ShowcaseTextSuggestionResponse>> SuggestTexts(
        [FromBody] SuggestShowcaseTextsRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var suggestion = await textSuggestions.SuggestAsync(orgId.Value, request.ToCommand(), cancellationToken);
        return Ok(ShowcaseTextSuggestionMapper.ToResponse(suggestion));
    }

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
        var (path, url) = await AddressAsync(orgId.Value, ensureAddress, cancellationToken);

        return Ok(ShowcaseContentMapper.ToResponse(view, path, url));
    }

    private async Task<(string? Path, string? Url)> AddressAsync(Guid orgId, bool ensure, CancellationToken cancellationToken)
    {
        if (ensure)
            await supplierService.EnsureShowcaseSlugAsync(orgId, cancellationToken);

        var profile = await supplierService.GetProfileAsync(orgId, cancellationToken);
        var path = string.IsNullOrEmpty(profile?.ShowcaseSlug) ? null : PublicSitePaths.SupplierShowcase(profile.ShowcaseSlug);
        return (path, path is null ? null : publicSiteLinks.TryPublicPage(path));
    }
}
