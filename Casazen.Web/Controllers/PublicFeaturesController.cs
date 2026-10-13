using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// Feature flags for the frontend (FD-20): <c>{ "otaPartnerApi": false }</c>, one camelCase key per
/// <see cref="FeatureFlags.All"/>. Anonymous: flags are not secret and the public pages may need them.
/// </summary>
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
