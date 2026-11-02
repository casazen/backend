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
    /// <summary>The period of the two numbers of the showcase: the requests of the last 30 days (decision VT-D15).</summary>
    private const int StatsDays = 30;

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

    /// <summary>
    /// "Annulla" after a publication: the version published before is published again (none, for the first publication: the showcase is a
    /// draft again), the draft stays as it is. 409 <c>showcase_undo_unavailable</c> once <c>undoUntil</c> has passed or something else was
    /// changed (a save, a discard, a pause).
    /// </summary>
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

    /// <summary>
    /// "Riattiva": reopens a paused showcase and, on one that is not paused, judges the bookings again (the account became ready after the
    /// publication). Opens the bookings when everything they need is ready. 422 <c>showcase_not_published</c> or
    /// <c>showcase_supplier_not_active</c>.
    /// </summary>
    [HttpPost("resume")]
    [ProducesResponseType(typeof(ShowcaseContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ShowcaseContentResponse>> Resume(CancellationToken cancellationToken) =>
        await RunAsync(orgId => content.ResumeAsync(orgId, cancellationToken), ensureAddress: false, cancellationToken);

    /// <summary>
    /// The short status of the showcase for the home and the checklist of the console: whether it is published, whether the draft has changes, whether
    /// it is paused, the public address, the two true numbers (<c>requests30d</c>: the requests that came from the showcase in the last 30 days,
    /// <c>pending30d</c>: how many of them still wait for an answer), what a publication would change, when it was published and last saved, and
    /// <c>completeness</c> (0 to 100: the share of the readiness items that are done).
    /// </summary>
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

    /// <summary>
    /// The two true numbers of the showcase editor: the requests that came from the showcase in the last 30 days and how many of them still wait for
    /// the supplier's answer. Visits and conversion are not counted (decision VT-D15).
    /// </summary>
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

    /// <summary>
    /// Whether the AI help of the texts can answer now (a provider is configured): when it cannot, the editor shows none of it and keeps the static
    /// starter. Reads no data of the supplier and calls no provider.
    /// </summary>
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

    /// <summary>The path and the absolute URL of the supplier's showcase; both <c>null</c> while it has no address (never activated).</summary>
    private async Task<(string? Path, string? Url)> AddressAsync(Guid orgId, bool ensure, CancellationToken cancellationToken)
    {
        if (ensure)
            await supplierService.EnsureShowcaseSlugAsync(orgId, cancellationToken);

        var profile = await supplierService.GetProfileAsync(orgId, cancellationToken);
        var path = string.IsNullOrEmpty(profile?.ShowcaseSlug) ? null : PublicSitePaths.SupplierShowcase(profile.ShowcaseSlug);
        return (path, path is null ? null : publicSiteLinks.TryPublicPage(path));
    }
}
