using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The amenities of a property as the booking site names, groups and draws them (DB-11a): the reference data the public property page needs
/// so it never shows a raw code. Anonymous, the same for everyone, static for a release.
/// </summary>
[ApiController]
[Route("api/public/amenities")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicAmenityCatalogController : ControllerBase
{
    private static readonly PublicAmenityCatalogResponse Catalog = PublicAmenityCatalogResponse.FromCatalog();

    /// <summary>
    /// Every amenity with its label in Italian and English, its icon and its group, and the five groups in display order. The names are the
    /// ones the public property DTOs carry in <c>amenities</c>. Cacheable for an hour.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PublicAmenityCatalogResponse), StatusCodes.Status200OK)]
    public ActionResult<PublicAmenityCatalogResponse> Get()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(Catalog);
    }
}
