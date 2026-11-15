using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The guest at the door (E22/F01/US02/T01, NF-24, NF-50): the QR code printed for a property leads to <c>/checkin/porta/{doorToken}</c>, where the guest types the
/// booking code (or the surname of the head guest) and the arrival date and gets the check-in link of its stay, to go on at the identity step. Public and anonymous: the proof of
/// the stay is the data the guest types, never the address. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/> (404 while it is off), off by default.
/// </summary>
/// <remarks>
/// Everything that does not open answers the one <see cref="NotFoundCode"/> 404, with the same body: an unknown or made-up door token, a stay of another property, a wrong code, surname
/// or date, a check-in already completed. The answer never says which part was wrong and never holds booking data besides the token. Five attempts a minute for each client IP and door token
/// (<see cref="RateLimitPolicies.PublicDoorCheckIn"/>, 429 <c>rate_limited</c> beyond). The answer holds a secret that opens the guest page, so it is private
/// (<c>Cache-Control: no-store</c>).
/// </remarks>
[ApiController]
[Route("api/public/checkin/door")]
[AllowAnonymous]
[PrivateAnswer]
public class PublicDoorCheckInController(IDoorCheckInService doorCheckIn) : ControllerBase
{
    /// <summary>Problem code of everything that does not open (404): the one answer, whatever part of the data was wrong.</summary>
    public const string NotFoundCode = "door_checkin_not_found";

    /// <summary>
    /// Finds the stay and issues its check-in link: <c>{ token }</c> to open <c>/checkin/{token}</c>. 404 <c>door_checkin_not_found</c> for every other case; 429 over the limit.
    /// </summary>
    [HttpPost("{doorToken}")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.PublicDoorCheckIn)]
    [ProducesResponseType(typeof(DoorCheckInResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<DoorCheckInResponse>> Open(
        string doorToken,
        [FromBody] DoorCheckInRequest request,
        CancellationToken cancellationToken)
    {
        var result = await doorCheckIn.OpenAsync(doorToken, request.ToInput(), cancellationToken);
        return result.Token is { } token
            ? Ok(new DoorCheckInResponse { Token = token })
            : this.ApiProblem(StatusCodes.Status404NotFound, NotFoundCode, "DoorCheckInNotFound");
    }
}
