using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicCalendarController(IOrgService orgService, IPublicCalendarService calendars) : ControllerBase
{
    [HttpGet("properties/{propertyId:guid}/calendar")]
    [ProducesResponseType(typeof(PublicCalendarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicCalendarResponse>> GetCalendar(
        Guid propertyId,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var calendar = await calendars.GetAsync(propertyId, from, to, cancellationToken);
        return Ok(PublicCalendarResponse.From(calendar));
    }

    [HttpGet("orgs/{slug}/properties/{propertySlugOrId}/calendar")]
    [ProducesResponseType(typeof(PublicCalendarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicCalendarResponse>> GetCalendarOfSite(
        [StringLength(100)] string slug,
        [StringLength(100)] string propertySlugOrId,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var calendar = await calendars.GetAsync(org.Id, propertySlugOrId, from, to, cancellationToken);
        return Ok(PublicCalendarResponse.From(calendar));
    }
}
