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
/// The search of the public booking site of one host (DB-07a): the houses for the dates and the guests chosen, the calendar of the whole site,
/// the places and the houses that look alike. Anonymous and read only. The houses are the published ones of the org of the site (a former slug
/// resolves, PL-04) and never another org's; the position is rounded and there is no address; a booking, a block and an iCal block look the same.
/// The price of a stay is the quote's own, to the cent.
/// </summary>
[ApiController]
[Route("api/public/orgs/{slug}")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicSiteSearchController(IOrgService orgService, IPublicSiteSearchService search) : ControllerBase
{
    /// <summary>
    /// Every published house of the site for a stay, in one call (<c>GET api/public/orgs/{slug}/stays</c>). For each house: the card, whether it is
    /// free for the stay or why not (<c>busy</c>, <c>min_nights</c>, <c>guests</c>, <c>pets</c>, <c>past</c>, <c>not_bookable</c> and, from the rules of the nights of the house (NF-36), <c>arrival_closed</c>, <c>departure_closed</c>, <c>max_nights</c>, <c>advance</c>), the first free
    /// check-in day for the houses that are taken, and the price: the total of the stay as the quote of the checkout adds it up (nights, cleaning,
    /// tourist tax when it can be calculated; <c>includesTouristTax</c> says <c>estimated</c> while the ages of the children are unknown) and the
    /// average price per night. The houses that are not free stay in the list, after the free ones. When no house is free, up to three nearby
    /// ranges of the same length with free houses come in <c>alternatives</c>. Without dates the houses come with their lowest price per night of
    /// the next 30 days. Filters this release cannot apply (labels of the houses, the rule about children) are not applied and named in
    /// <c>unsupportedFilters</c>. 404 for a site that does not resolve; 422 <c>public_stays_dates_invalid</c> for dates that are not both given, not in
    /// order or longer than 366 nights; 400 for a value out of range; 429 per client IP. Kept for a few seconds (<c>generatedAt</c> says when it was
    /// worked out).
    /// </summary>
    [HttpGet("stays")]
    [EnableRateLimiting(RateLimitPolicies.PublicStaySearch)]
    [ProducesResponseType(typeof(PublicStaysResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicStaysResult>> GetStays(
        [StringLength(100)] string slug,
        [FromQuery] PublicStaysQuery query,
        CancellationToken cancellationToken)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        return Ok(await search.SearchStaysAsync(org, query.ToCriteria(), cancellationToken));
    }

    [HttpGet("calendar")]
    [EnableRateLimiting(RateLimitPolicies.PublicStaySearch)]
    [ProducesResponseType(typeof(PublicSiteCalendar), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSiteCalendar>> GetCalendar(
        [StringLength(100)] string slug,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery, Range(1, 50)] int adults = 2,
        [FromQuery, Range(0, 50)] int children = 0,
        CancellationToken cancellationToken = default)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        return Ok(await search.GetSiteCalendarAsync(org, from, to, adults, children, cancellationToken));
    }

    [HttpGet("destinations")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicDestination>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PublicDestination>>> GetDestinations(
        [StringLength(100)] string slug,
        CancellationToken cancellationToken)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        return Ok(await search.GetDestinationsAsync(org.Id, cancellationToken));
    }

    /// <summary>
    /// Houses of the site that look like another (<c>GET api/public/orgs/{slug}/properties/{slugOrId}/similar?max=3</c>, 6 at most): the same kind of
    /// place, the same comune, a price near; never the house itself. The cards carry the lowest price per night, no stay. 404 for a site or a
    /// property that is not on it.
    /// </summary>
    [HttpGet("properties/{propertySlugOrId}/similar")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicStayCard>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PublicStayCard>>> GetSimilar(
        [StringLength(100)] string slug,
        [StringLength(100)] string propertySlugOrId,
        [FromQuery, Range(1, PublicSiteSearch.MaxSimilar)] int max = PublicSiteSearch.DefaultSimilar,
        CancellationToken cancellationToken = default)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var similar = await search.GetSimilarAsync(org.Id, propertySlugOrId, max, cancellationToken);
        return similar is null ? NotFound() : Ok(similar);
    }
}
