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
/// The public search of CasaZen across ALL the booking sites (<c>/search</c> of the app and the form of the home): the same contract as the
/// search of one site (<see cref="PublicSiteSearchController"/>, DB-07a) over the published houses of every active org that can take a
/// booking, each with the slug of its own site. Anonymous and read only. The price of a stay is the quote's own, to the cent; the position of a
/// house is rounded to about a kilometre and there is no address; a booking, a block and an iCal block look the same. The old
/// <c>GET api/properties/search</c> (no dates, no total) stays as it is for the callers that use it.
/// </summary>
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

    /// <summary>
    /// The days of all the sites, for the «Quando» field (<c>GET api/public/stays/calendar</c>): for each day from <c>from</c> (default today)
    /// to <c>to</c> (excluded, default 12 months later; 13 at most) whether some house that sleeps the party is free for the night, the
    /// cheapest night among those houses in cents and the shortest minimum stay among them; with <c>where</c> only the houses of that place
    /// count. Days before today are not listed. 422 <c>public_site_calendar_range_invalid</c>.
    /// </summary>
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

    /// <summary>
    /// The places of all the sites, for the «Dove» field (<c>GET api/public/stays/destinations</c>): the comuni of the houses that can be
    /// booked with how many houses each has, the most first (100 at most); <c>id</c> is a value the <c>where</c> filter understands.
    /// </summary>
    [HttpGet("destinations")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicDestination>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PublicDestination>>> GetDestinations(CancellationToken cancellationToken) =>
        Ok(await search.GetPlatformDestinationsAsync(cancellationToken));
}
