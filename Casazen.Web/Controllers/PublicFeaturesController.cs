using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/features")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicFeaturesController(IFeatureFlags featureFlags) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyDictionary<string, bool>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyDictionary<string, bool>> Get() =>
        Ok(FeatureFlags.All.ToDictionary(JsonNamingPolicy.CamelCase.ConvertName, featureFlags.IsEnabled));
}
