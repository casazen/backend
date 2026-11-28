using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Reviews;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/reviews")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class ReviewsController(
    IHostReviewService reviews,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<HostReviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<HostReviewDto>>> List(
        [FromQuery] Guid? propertyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ReviewRules.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await reviews.ListAsync(scope, propertyId, page, pageSize, cancellationToken);
        return Ok(new PagedResultDto<HostReviewDto>
        {
            Items = result.Items.Select(HostReviewDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }

    [HttpPost("{id:guid}/reply")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(HostReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostReviewDto>> Reply(Guid id, [FromBody] ReplyReviewRequest request, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return NotFound();

        return Ok(HostReviewDto.From(await reviews.ReplyAsync(id, request.Text, cancellationToken)));
    }

    [HttpPost("{id:guid}/report")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(HostReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostReviewDto>> Report(Guid id, [FromBody] ReportReviewRequest request, CancellationToken cancellationToken)
    {
        if (!await CanWriteAsync(id, cancellationToken))
            return NotFound();

        var reported = await reviews.ReportAsync(id, request.Reason!.Value, request.Note, User.GetUserId(), cancellationToken);
        return Ok(HostReviewDto.From(reported));
    }

    private async Task<bool> CanWriteAsync(Guid reviewId, CancellationToken cancellationToken)
    {
        var propertyId = await reviews.FindPropertyIdAsync(reviewId, cancellationToken);
        var resource = propertyId is null ? null : await hostResources.ForPropertyAsync(propertyId.Value, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write);
    }
}
