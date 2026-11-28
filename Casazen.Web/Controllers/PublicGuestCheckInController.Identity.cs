using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

public partial class PublicGuestCheckInController
{
    public const string IdentityStateExtension = "state";

    /// <summary>
    /// Opens the identity step of the session, once (NF-188, AC-NF188-1, AC-NF188-2): <c>{ noticeVersion }</c> is the version of the biometric consent text the page
    /// shows (<c>GET .../legal-texts</c>), and it must be the one the registry serves now, otherwise 422 <c>notice_version_unknown</c> and nothing is recorded. The first
    /// call records the time, the version and the hash of that text, the family of the browser (from the <c>User-Agent</c>, which is never stored or logged) and binds the
    /// verification to the guest who verifies; later calls change nothing. Answers <c>{ state }</c>. A closed verification answers 409 <c>identity_already_closed</c> with
    /// the state; a verification that already passed or is with a person is not opened (<c>state.opened</c> false). 404 for an unknown, expired or completed link.
    /// </summary>
    [HttpPost("{token}/identity/open")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [EnableRateLimiting(RateLimitPolicies.GuestIdentity)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<IdentityOpenResponse>> OpenIdentity(string token, [FromBody] IdentityOpenRequest request)
    {
        if (!SelfHostedIdentityOn())
            return NotFound();

        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        var result = await verificationFlow.OpenIdentityAsync(session, request.NoticeVersion, RequestChannel(), HttpContext.RequestAborted);
        return result switch
        {
            { Status: IdentityOpenStatus.Closed, State: { } closed } => IdentityClosedProblem(closed),
            { Status: IdentityOpenStatus.NoticeVersionUnknown } =>
                this.ApiProblem(StatusCodes.Status422UnprocessableEntity, IdentityApiErrors.NoticeVersionUnknown, "CheckInIdentityNoticeVersionUnknown"),
            { Status: IdentityOpenStatus.Answered, State: { } state } => Ok(new IdentityOpenResponse { State = PublicIdentityVerificationStateDto.From(state) }),
            _ => NotFound(),
        };
    }

    private bool SelfHostedIdentityOn() =>
        featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification) && featureFlags.IsEnabled(FeatureFlags.CheckInIdentitySelfHosted);

    private string RequestChannel() => IdentityChannelClassifier.Classify(Request.Headers.UserAgent.ToString());

    private async Task<ActionResult?> RefuseClosedIdentityAsync(GuestCheckInSession session)
    {
        if (!SelfHostedIdentityOn() || session.IdentityClosedAt is null)
            return null;

        return IdentityClosedProblem(await verificationFlow.GetStateAsync(session, HttpContext.RequestAborted));
    }

    private ObjectResult IdentityClosedProblem(IdentityVerificationState state)
    {
        var problem = this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.IdentityAlreadyClosed, "CheckInIdentityAlreadyClosed");
        if (problem.Value is ProblemDetails details)
            details.Extensions[IdentityStateExtension] = PublicIdentityVerificationStateDto.From(state);

        return problem;
    }
}
