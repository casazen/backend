using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The calendar of a property on the public booking site (DB-03b): the night that starts on each day with its price and whether a
/// stay can start on it. Anonymous, rate limited per client IP; dates and amounts only.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicCalendarController(IOrgService orgService, IPublicCalendarService calendars) : ControllerBase
{
    /// <summary>
    /// The days of a property from <c>from</c> (included, default today in Europe/Rome) to <c>to</c> (excluded, default 12 months after
    /// <c>from</c>; 13 months at most). Each day says whether a stay can start that night (no booking, hold, request, iCal or manual block
    /// takes it, the same rule that refuses a booking), what the night costs in cents (the number the quote adds up for that night) and the
    /// fewest nights of a stay that starts on it. Days before today are not listed. It does not tell a booking from a block, nor who stays.
    /// 404 <c>public_property_not_found</c> for a property that does not exist or is not published; 422 <c>public_calendar_range_invalid</c>
    /// for an empty range or one longer than 13 months; 429 per client IP.
    /// </summary>
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

    /// <summary>
    /// The same calendar for a property of a site, by the slug or the id of the property: <c>GET api/public/orgs/{slug}/properties/{slugOrId}/calendar</c>.
    /// 404 for an org that does not resolve (its former slugs too, PL-04) and for a property that is not on that site.
    /// </summary>
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
