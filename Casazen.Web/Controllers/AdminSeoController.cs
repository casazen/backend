using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/seo")]
[Authorize(Policy = "AdminOnly")]
public class AdminSeoController(
    ISeoContentService seoContentService,
    IBackgroundJobClient backgroundJobClient,
    ILogger<AdminSeoController> logger) : ControllerBase
{
    [HttpGet("pages")]
    [ProducesResponseType(typeof(PagedResultDto<SeoPageAdminDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<SeoPageAdminDto>>> ListPages(
        [FromQuery] LegalReviewStatus? legalReviewStatus = null,
        [FromQuery] SeoPageType? pageType = null,
        [FromQuery] string? comuneCode = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Min(pageSize, 100);
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

    [HttpPost("generate")]
    [ProducesResponseType(typeof(SeoGenerateAcceptedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SeoGenerateAcceptedDto> Generate([FromBody] SeoGenerateRequestDto request)
    {
        if (request.ComuneCodes.Count == 0)
            return BadRequest(new { error = "comuneCodes is required." });

        var pageTypes = request.PageTypes?.Count > 0
            ? request.PageTypes
            : new[] { SeoPageType.ComplianceGuide, SeoPageType.TouristTaxCalc };

        if (pageTypes.Any(t => t == SeoPageType.SupplierMicrosite))
        {
            logger.LogInformation("SupplierMicrosite generation deferred until supplier marketplace Phase 0");
        }

        var filteredTypes = pageTypes.Where(t => t != SeoPageType.SupplierMicrosite).ToList();
        var jobId = backgroundJobClient.Enqueue<SeoPageGenerationJob>(job =>
            job.ExecuteAsync(request.ComuneCodes, filteredTypes, request.ForceRegenerate));

        logger.LogInformation("Enqueued SEO generation job {JobId} for {ComuneCount} comuni", jobId, request.ComuneCodes.Count);

        return Accepted(new SeoGenerateAcceptedDto(
            jobId,
            DateTime.UtcNow,
            request.ComuneCodes.Count,
            request.ComuneCodes.Count * filteredTypes.Count));
    }

    [HttpPatch("pages/{id:guid}/review-status")]
    [ProducesResponseType(typeof(SeoPageAdminDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SeoPageAdminDto>> UpdateReviewStatus(
        Guid id,
        [FromBody] UpdateSeoReviewStatusRequest body,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await seoContentService.UpdateReviewStatusAsync(
                id,
                body.LegalReviewStatus,
                body.CounselApproved,
                cancellationToken);

            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("COUNSEL_REQUIRED"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpGet("budget")]
    [ProducesResponseType(typeof(PlatformAiBudgetDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PlatformAiBudgetDto>> GetBudget(CancellationToken cancellationToken)
    {
        return Ok(await seoContentService.GetPlatformAiBudgetAsync(cancellationToken));
    }
}

public record UpdateSeoReviewStatusRequest(LegalReviewStatus LegalReviewStatus, bool CounselApproved = false);
