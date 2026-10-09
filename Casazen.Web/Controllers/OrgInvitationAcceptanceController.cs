using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The two calls of the person who was invited (AM-02): what the link is for, and the acceptance. Both take the secret
/// token <b>in the body</b>, never in a URL (URLs end up in logs and history). Behind the <c>OrgTeam</c> flag: 404 while it is
/// off. Runbook <c>docs/runbooks/org-team.md</c>.
/// </summary>
[ApiController]
[Route("api/org-invitations")]
[FeatureGate(FeatureFlags.OrgTeam)]
public class OrgInvitationAcceptanceController(
    IOrgInvitationService invitations,
    IOnboardingService onboardingService,
    IUserService userService,
    IAccountEmailResolver accountEmails) : ControllerBase
{
    /// <summary>
    /// What an invitation link is for: the org, the address it is for, the role, the areas and the expiry, to show before
    /// the person signs in and accepts. Anonymous and rate limited. Every link that does not work (malformed, unknown, replaced
    /// by a newer one, expired, used, revoked) gets the <b>same</b> answer, 410 <c>invitation_invalid</c>: a stranger
    /// learns nothing. The response is never cached.
    /// </summary>
    [HttpPost("lookup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicInvitationLookup)]
    [ProducesResponseType(typeof(OrgInvitationLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OrgInvitationLookupResponse>> Lookup(
        [FromBody] OrgInvitationLookupRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var preview = await invitations.LookupAsync(request.Token, cancellationToken);
        return preview is null
            ? this.ApiProblem(StatusCodes.Status410Gone, OrgInvitationErrors.Invalid, "InvitationInvalid")
            : Ok(OrgInvitationLookupResponse.From(preview));
    }

    /// <summary>
    /// The signed-in account accepts the invitation and becomes a member of the org with the invited role. It must have the
    /// invited email, verified (403 <c>invitation_email_mismatch</c>, <c>invitation_email_not_verified</c>), must not be
    /// a platform admin (403 <c>invitation_platform_admin</c>) and sends the four consents of the onboarding again for this
    /// org (400 <c>consents_incomplete</c>, <c>stale_documents</c>). A person with no org joins at once; one with an empty
    /// and unbilled org of its own leaves it; anyone else is refused, 409 <c>invitation_user_has_organization</c>, and
    /// nothing is merged. 410 <c>invitation_invalid</c>, <c>invitation_expired</c>, <c>invitation_used</c>,
    /// <c>invitation_revoked</c> when the link no longer works. Sending it twice finds it done.
    /// </summary>
    [HttpPost("accept")]
    [Authorize(Policy = CasazenPolicies.Authenticated)]
    [ProducesResponseType(typeof(AcceptOrgInvitationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<ActionResult<AcceptOrgInvitationResponse>> Accept(
        [FromBody] AcceptOrgInvitationRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // The consents first: nothing is read or written for a request that cannot be accepted anyway.
        var consents = request.Consents.ToInput();
        var (consentsValid, consentsError) = onboardingService.ValidateConsents(consents, requireConsents: true);
        if (!consentsValid || consents is null)
            return ToConsentProblem(consentsError);

        // The email and its verification come from the token or from Auth0, never from the body.
        var (email, emailVerified) = await accountEmails.ResolveAsync(User, userId, cancellationToken);

        // The acceptance page lives outside the app shell: the account row may not exist yet.
        await userService.GetCurrentUserAsync(
            userId,
            email ?? string.Empty,
            User.FindFirst("given_name")?.Value ?? User.FindFirst("name")?.Value?.Split(' ').FirstOrDefault() ?? string.Empty,
            User.FindFirst("family_name")?.Value ?? User.FindFirst("name")?.Value?.Split(' ').Skip(1).FirstOrDefault() ?? string.Empty);

        var accepted = await invitations.AcceptAsync(
            new AcceptOrgInvitation(
                request.Token,
                userId,
                email ?? string.Empty,
                emailVerified,
                User.GetRoles().Contains("Admin"),
                consents,
                ClientIp.GetString(HttpContext)),
            cancellationToken);

        return Ok(AcceptOrgInvitationResponse.From(accepted));
    }

    /// <summary>The same answers as the onboarding: <c>consents_incomplete</c>, or <c>stale_documents</c> with the list of documents.</summary>
    private ActionResult ToConsentProblem(ConsentValidationError? error)
    {
        if (error?.Type != ConsentValidationErrorType.StaleVersion)
            return this.ApiProblem(StatusCodes.Status400BadRequest, UsersController.ConsentsIncompleteCode, "ConsentsIncomplete");

        var problem = ApiProblemDetails.Create(
            HttpContext, StatusCodes.Status400BadRequest, UsersController.StaleDocumentsCode, "ConsentsStale");
        problem.Extensions["staleDocuments"] = error.StaleDocuments ?? [];
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }
}
