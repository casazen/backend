using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/stays")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicPlatformSearchController(IPublicSiteSearchService search) : ControllerBase
{
    /// <summary>
    /// The houses of all the sites for a stay (<c>GET api/public/stays</c>), with the parameters of <c>GET api/public/orgs/{slug}/stays</c>:
    /// the dates and the party, <c>where</c> (what the visitor typed: the comune, or a word of the zone the host wrote), the filters and the
    /// sort (<c>recommended</c> is the best rated first), and <c>page</c> / <c>pageSize</c> (24 by default, 50 at most). Only the houses that
    /// are free for the stay are listed (<c>totalCount</c> counts the ones that pass the filters, <c>availableCount</c> the free ones); when
    /// none is free, up to three nearby ranges that are come in <c>alternatives</c>. Each card carries <c>orgSlug</c> and <c>hostName</c>
    /// (the site is <c>/book/{orgSlug}/property/{slug}</c>) and <c>facets</c> says what the controls can offer for the place (price and size
    /// reach, guests, pets, labels). 422 <c>public_stays_dates_invalid</c> for dates that are not both given, not in order or longer than 366
    /// nights; 400 for a value out of range; 429 per client IP. Kept for a few seconds (<c>generatedAt</c> says when it was worked out).
    /// </summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.PublicStaySearch)]
    [ProducesResponseType(typeof(PublicStaysResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicStaysResult>> GetStays([FromQuery] PublicStaysQuery query, CancellationToken cancellationToken) =>
        Ok(await search.SearchPlatformStaysAsync(query.ToCriteria(PublicStaysScope.Platform), cancellationToken));

    [HttpGet("calendar")]
    [EnableRateLimiting(RateLimitPolicies.PublicStaySearch)]
    [ProducesResponseType(typeof(PublicSiteCalendar), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSiteCalendar>> GetCalendar(
        [FromQuery, StringLength(100)] string? where = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery, Range(1, 50)] int adults = 2,
        [FromQuery, Range(0, 50)] int children = 0,
        CancellationToken cancellationToken = default) =>
        Ok(await search.GetPlatformCalendarAsync(where, from, to, adults, children, cancellationToken));

    [HttpGet("destinations")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicDestination>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PublicDestination>>> GetDestinations(CancellationToken cancellationToken) =>
        Ok(await search.GetPlatformDestinationsAsync(cancellationToken));
}
