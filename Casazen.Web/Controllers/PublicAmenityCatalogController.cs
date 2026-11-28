using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/amenities")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicAmenityCatalogController : ControllerBase
{
    private static readonly PublicAmenityCatalogResponse Catalog = PublicAmenityCatalogResponse.FromCatalog();

    [HttpGet]
    [ProducesResponseType(typeof(PublicAmenityCatalogResponse), StatusCodes.Status200OK)]
    public ActionResult<PublicAmenityCatalogResponse> Get()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(Catalog);
    }
}
