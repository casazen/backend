using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/reviews/reports")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminReviewsController(IAdminReviewService reviews) : ControllerBase
{
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
