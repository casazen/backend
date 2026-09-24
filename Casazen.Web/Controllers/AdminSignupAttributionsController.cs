using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Where host signups come from (SE-03, A8-03): the attributions recorded after the first onboarding, for the platform
/// admin. Read-only; the dashboard widget is task SE-04.
/// </summary>
[ApiController]
[Route("api/admin/signup-attributions")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminSignupAttributionsController(ISignupAttributionService signupAttributionService) : ControllerBase
{
    private const int MaxDays = 3660;
    private const int MaxPageSize = 100;

    /// <summary>
    /// Attributions of every org, newest first. <paramref name="days"/> limits them to the last N days (default 30,
    /// at most 10 years), <paramref name="comuneCode"/> to one comune (ISTAT code).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<SignupAttributionAdminDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<SignupAttributionAdminDto>>> List(
        [FromQuery] int days = 30,
        [FromQuery] string? comuneCode = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, MaxDays);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, total) = await signupAttributionService.ListAsync(
            DateTime.UtcNow.AddDays(-days),
            comuneCode,
            page,
            pageSize,
            cancellationToken);

        return Ok(new PagedResultDto<SignupAttributionAdminDto>
        {
            Items = items.Select(SignupAttributionAdminDto.From).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }
}
