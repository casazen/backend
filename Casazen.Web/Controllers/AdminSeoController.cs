using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Admin SEO dashboard (US-020, SE-01): pages, review (preview, approve, withdraw), generation, AI budget. Every text is
/// published only by an explicit approval of the revision the admin read, recorded in the audit (A8-04, A8-05, A8-21).
/// </summary>
[ApiController]
[Route("api/admin/seo")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminSeoController(
    ISeoContentService seoContentService,
    ISeoComuneCatalog comuneCatalog,
    IAiRuntimeSettings aiSettings,
    IBackgroundJobClient backgroundJobClient,
    ILogger<AdminSeoController> logger) : ControllerBase
{
    public const int MaxPageSize = 100;

    /// <summary>Pages, newest first. <c>page</c> 1-based, <c>pageSize</c> 1-100 (400 otherwise).</summary>
    [HttpGet("pages")]
    [ProducesResponseType(typeof(PagedResultDto<SeoPageAdminDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResultDto<SeoPageAdminDto>>> ListPages(
        [FromQuery] LegalReviewStatus? legalReviewStatus = null,
        [FromQuery] SeoPageType? pageType = null,
        [FromQuery] string? comuneCode = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, MaxPageSize)] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await seoContentService.ListPagesAsync(
            legalReviewStatus,
            pageType,
            comuneCode,
            page,
            pageSize,
            cancellationToken);

        return Ok(new PagedResultDto<SeoPageAdminDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>Review screen: the published text and the one waiting for a review (sanitized), and the review audit.</summary>
    [HttpGet("pages/{id:guid}")]
    [ProducesResponseType(typeof(SeoPageAdminDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoPageAdminDetailDto>> GetPage(Guid id, CancellationToken cancellationToken)
    {
        var page = await seoContentService.GetAdminPageAsync(id, cancellationToken);
        return page is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFoundDetail")
            : Ok(page);
    }

    /// <summary>
    /// Publishes the revision the admin read (<c>revisionId</c>): 409 when a newer revision arrived meanwhile, 422 when it
    /// has no publishable text or when the page needs the legal review confirmation (<c>counselApproved</c>).
    /// </summary>
    [HttpPost("pages/{id:guid}/approve")]
    [ProducesResponseType(typeof(SeoPageAdminDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SeoPageAdminDto>> Approve(
        Guid id,
        [FromBody] ApproveSeoRevisionRequest body,
        CancellationToken cancellationToken)
    {
        var updated = await seoContentService.ApproveRevisionAsync(
            new SeoApproveRevisionCommand(id, body.RevisionId!.Value, body.CounselApproved, body.Note, ActorUserId()),
            cancellationToken);
        return Ok(updated);
    }

    /// <summary>Withdraws the published text: the page goes back to draft and leaves the public site and the sitemap.</summary>
    [HttpPost("pages/{id:guid}/withdraw")]
    [ProducesResponseType(typeof(SeoPageAdminDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SeoPageAdminDto>> Withdraw(
        Guid id,
        [FromBody] WithdrawSeoPageRequest body,
        CancellationToken cancellationToken)
    {
        var updated = await seoContentService.WithdrawAsync(
            new SeoWithdrawCommand(id, body.Note, ActorUserId()),
            cancellationToken);
        return Ok(updated);
    }

    /// <summary>Auth0 subject of the admin: AdminOnly guarantees an authenticated user, whose token always has one.</summary>
    private string ActorUserId() =>
        User.GetUserId() ?? throw new UnauthorizedAccessException("Admin without subject claim");

    /// <summary>The pilot comuni of the SEO pages, as found in the official ISTAT list (empty until it is imported).</summary>
    [HttpGet("comuni")]
    [ProducesResponseType(typeof(IReadOnlyList<SeoComuneRegistryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SeoComuneRegistryDto>>> ListComuni(CancellationToken cancellationToken)
    {
        var items = (await comuneCatalog.GetPilotsAsync(cancellationToken))
            .Select(c => new SeoComuneRegistryDto(c.Code, c.Name, c.RegionSlug, c.ComuneSlug))
            .ToList();
        return Ok(items);
    }

    /// <summary>
    /// Queues AI generation of SEO pages. Rate limited per user (<see cref="AiRateLimitAttribute"/>); the job stops at
    /// the first page the monthly AI budget cannot cover. Every generated text is a draft waiting for a review: nothing
    /// is approved automatically (SE-01).
    /// </summary>
    [HttpPost("generate")]
    [AiRateLimit]
    [ProducesResponseType(typeof(SeoGenerateAcceptedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SeoGenerateAcceptedDto>> Generate(
        [FromBody] SeoGenerateRequestDto request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> comuneCodes = request.ComuneCodes.Count > 0
            ? request.ComuneCodes
            : (await comuneCatalog.GetPilotsAsync(cancellationToken)).Select(c => c.Code).ToList();
        if (comuneCodes.Count == 0)
        {
            // No comune named and none of the pilots found: the official list is not imported (never "0 pages queued").
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                ComuneErrorCodes.DatasetUnavailable,
                ComuneErrorCodes.DatasetUnavailableMessageKey);
        }

        var pageTypes = request.PageTypes?.Count > 0
            ? request.PageTypes
            : new[] { SeoPageType.ComplianceGuide, SeoPageType.TouristTaxCalc };

        if (pageTypes.Any(t => t == SeoPageType.SupplierMicrosite))
        {
            logger.LogInformation("SupplierMicrosite generation deferred until supplier marketplace Phase 0");
        }

        var filteredTypes = pageTypes.Where(t => t != SeoPageType.SupplierMicrosite).ToList();
        var jobId = backgroundJobClient.Enqueue<SeoPageGenerationJob>(job =>
            job.ExecuteAsync(comuneCodes, filteredTypes, request.ForceRegenerate));

        logger.LogInformation("Enqueued SEO generation job {JobId} for {ComuneCount} comuni", jobId, comuneCodes.Count);

        return Accepted(new SeoGenerateAcceptedDto(
            jobId,
            DateTime.UtcNow,
            comuneCodes.Count,
            comuneCodes.Count * filteredTypes.Count));
    }

    /// <summary>
    /// The AI provider in effect. The API key is never returned: only whether one is set and its last characters.
    /// </summary>
    [HttpGet("ai")]
    [ProducesResponseType(typeof(AiAdminSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiAdminSettingsDto>> GetAiSettings(CancellationToken cancellationToken)
    {
        return Ok(await aiSettings.GetAdminViewAsync(cancellationToken));
    }

    /// <summary>
    /// Saves the provider an admin chose. A key sent here replaces the stored one and is encrypted at rest; it takes
    /// effect on the next generation, without a restart. <c>Stub</c> sends nothing to an external provider.
    /// </summary>
    [HttpPut("ai")]
    [ProducesResponseType(typeof(AiAdminSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AiAdminSettingsDto>> UpdateAiSettings(
        [FromBody] UpdateAiSettingsRequest body,
        CancellationToken cancellationToken)
    {
        var updated = await aiSettings.UpdateAsync(
            new UpdateAiSettingsCommand(body.Provider, body.ApiKey, body.ClearApiKey, body.Model, body.BaseUrl, ActorUserId()),
            cancellationToken);
        logger.LogInformation(
            "AI provider set to {Provider} by {Actor}; external active: {ExternalActive}",
            updated.Provider, ActorUserId(), updated.ExternalActive);
        return Ok(updated);
    }

    /// <summary>
    /// Stores a draft the admin wrote for this page. It is not published: the admin still approves the revision they read.
    /// </summary>
    [HttpPost("pages/{id:guid}/draft")]
    [ProducesResponseType(typeof(SeoPageAdminDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SeoPageAdminDetailDto>> SaveDraft(
        Guid id,
        [FromBody] SaveSeoDraftRequest body,
        CancellationToken cancellationToken)
    {
        var detail = await seoContentService.SaveManualDraftAsync(
            new SaveSeoDraftCommand(id, body.BodyHtml, body.Title, body.MetaDescription, ActorUserId()),
            cancellationToken);
        return Ok(detail);
    }

    [HttpGet("budget")]
    [ProducesResponseType(typeof(PlatformAiBudgetDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlatformAiBudgetDto>> GetBudget(CancellationToken cancellationToken)
    {
        return Ok(await seoContentService.GetPlatformAiBudgetAsync(cancellationToken));
    }
}

/// <summary>
/// Approval of a revision. <c>CounselApproved</c>: the admin confirms the legal review of the text (explicit, never a
/// default; required for the first 100 pages). <c>Note</c>: optional, kept in the audit.
/// </summary>
public record ApproveSeoRevisionRequest(
    [param: Required] Guid? RevisionId,
    bool CounselApproved = false,
    [param: MaxLength(1000)] string? Note = null);

/// <summary>Withdrawal of the published text; <c>Note</c> (optional) is kept in the audit.</summary>
public record WithdrawSeoPageRequest([param: MaxLength(1000)] string? Note = null);

/// <summary>
/// Provider saved from the admin panel. <c>ApiKey</c> empty: keep the stored key. <c>ClearApiKey</c>: drop it.
/// <c>Model</c> empty: use the model of the server configuration.
/// </summary>
public record UpdateAiSettingsRequest(
    [param: Required, MaxLength(32)] string Provider,
    [param: MaxLength(500)] string? ApiKey = null,
    bool ClearApiKey = false,
    [param: MaxLength(100)] string? Model = null,
    [param: MaxLength(300)] string? BaseUrl = null);

/// <summary>A draft written by the admin. HTML or plain text; it stays unpublished until the review.</summary>
public record SaveSeoDraftRequest(
    [param: Required, MaxLength(100_000)] string BodyHtml,
    [param: MaxLength(300)] string? Title = null,
    [param: MaxLength(500)] string? MetaDescription = null);
