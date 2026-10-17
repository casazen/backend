using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The reports of the hosts about reviews and the decision of the staff (DB-12a): the queue and the answer, by API only (the screen of the
/// staff console is another task). A review is taken down only here, with a reason that is written down.
/// </summary>
[ApiController]
[Route("api/admin/reviews/reports")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminReviewsController(IAdminReviewService reviews) : ControllerBase
{
    /// <summary>The reports of every org, oldest first, a page at a time; <c>status</c> (<c>Open</c>, <c>Kept</c>, <c>Removed</c>) filters them.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<AdminReviewReportDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<AdminReviewReportDto>>> List(
        [FromQuery] ReviewReportStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ReviewRules.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await reviews.ListReportsAsync(status, page, pageSize, cancellationToken);
        return Ok(new PagedResultDto<AdminReviewReportDto>
        {
            Items = result.Items.Select(AdminReviewReportDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }

    /// <summary>
    /// Decides an open report: <c>Keep</c> leaves the review public, <c>Remove</c> takes it down. The reason is required and kept with the
    /// report. 409 <c>review_report_already_decided</c> for a report that was decided.
    /// </summary>
    [HttpPost("{id:guid}/decide")]
    [ProducesResponseType(typeof(AdminReviewReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminReviewReportDto>> Decide(
        Guid id,
        [FromBody] DecideReviewReportRequest request,
        CancellationToken cancellationToken)
    {
        var report = await reviews.DecideAsync(id, request.Decision!.Value, request.Reason, User.GetUserId(), cancellationToken);
        return Ok(AdminReviewReportDto.From(report));
    }
}
