using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The presence of the self-hosted identity step (E72/F01/US01, NF-192, ADR-013-R17). This part of the controller holds the endpoints of the presence; its services
/// come as <c>[FromServices]</c> parameters, so the constructor of the class does not change, and they are registered in
/// <c>IdentityVerificationServiceCollectionExtensions.Presence.cs</c>.
/// </summary>
public partial class PublicGuestCheckInController
{
    /// <summary>
    /// A presence challenge for the next attempt of the guest who verifies (E72/F01/US01/T01): <c>{ challengeToken, steps, stepMs, leadInMs, expiresAt, targetFps,
    /// maxFrames, maxFrameSide }</c>. The steps are several moves drawn at random from the policy (more than one bit), the proof is bound to the session, the attempt and
    /// the challenge, and a new call replaces the previous challenge, whose proof stops working. Never cached. 404 for an unknown, expired or completed link and, before
    /// anything is read, while <see cref="FeatureFlags.CheckInIdentityVerification"/> or <see cref="FeatureFlags.CheckInIdentitySelfHosted"/> is off; 409
    /// <c>identity_already_closed</c> once the verification is closed; 409 <c>presence_challenge_unavailable</c> with the <c>state</c> of the verification while the session is
    /// not ready for the presence (the identity step is not opened, the consent is not the current one, the document is not complete, a person already has the
    /// verification), or the policy or the models are not available: the page then offers the assisted path, which is always open.
    /// </summary>
    [HttpPost("{token}/presence-challenge")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [EnableRateLimiting(RateLimitPolicies.GuestIdentity)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<PresenceChallengeDto>> IssuePresenceChallenge(string token, [FromServices] IIdentityPresenceFlow presenceFlow)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        // A closed verification takes no presence step any more; the request for a person stays open.
        if (session.IdentityClosedAt is not null)
            return this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.IdentityAlreadyClosed, "CheckInIdentityAlreadyClosed");

        var challenge = await presenceFlow.IssuePresenceChallengeAsync(session, HttpContext.RequestAborted);
        if (challenge is not null)
            return Ok(PresenceChallengeDto.From(challenge));

        var state = await verificationFlow.GetStateAsync(session, HttpContext.RequestAborted);
        var unavailable = this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.PresenceChallengeUnavailable, "CheckInPresenceChallengeUnavailable");
        if (unavailable.Value is ProblemDetails problem)
            problem.Extensions["state"] = PublicIdentityVerificationStateDto.From(state);

        return unavailable;
    }
}
