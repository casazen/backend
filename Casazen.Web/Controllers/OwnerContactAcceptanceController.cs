using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The acceptance of the invitation of a contact an owner lets into its portal (E07/F07/US01/T02, AC-NF111-4): the endpoint behind the page
/// <c>/portale-proprietari/contatto</c>, which keeps the token through the Auth0 sign-in and calls it once. The secret token is in the body, never in a URL.
/// </summary>
/// <remarks>
/// <para><b>Who may call it.</b> Any signed-in account, as the acceptance of an owner invitation: the contact has no portal claim yet, so no policy can name
/// it. What decides is the secret token, the e-mail of the account (from the token or from Auth0, <b>verified</b>, and equal to the invited address) and the
/// state of the invitation. The account becomes an identity of the portal of its own, never the owner's login.</para>
/// <para>Behind <c>Features:AgencyTier</c> like the rest of the agency owner portal: 404 while it is off. The answer is private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/owner-contacts")]
[FeatureGate(FeatureFlags.AgencyTier)]
[PrivateAnswer]
[EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
public sealed class OwnerContactAcceptanceController(
    IOwnerCollaborationService collaboration,
    IAccountEmailResolver accountEmails) : ControllerBase
{
    /// <summary>
    /// The signed-in account accepts the invitation and becomes the contact. 403 <c>owner_contact_email_not_verified</c> and
    /// <c>owner_contact_email_mismatch</c>; 410 <c>owner_contact_invitation_invalid</c>, <c>_expired</c>, <c>_used</c>, <c>_revoked</c>. Sending it twice
    /// finds it done and answers the same. The answer carries the claims of the portal and whether they reached the identity provider.
    /// </summary>
    [HttpPost("accept")]
    [Authorize(Policy = CasazenPolicies.Authenticated)]
    [ProducesResponseType(typeof(OwnerContactAccepted), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<ActionResult<OwnerContactAccepted>> Accept([FromBody] AcceptOwnerContactBody body, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // The email and its verification come from the token or from Auth0, never from the body.
        var (email, emailVerified) = await accountEmails.ResolveAsync(User, userId, cancellationToken);
        var (_, firstName, lastName) = AccountEmailResolver.ReadProfile(User);
        return Ok(await collaboration.AcceptContactAsync(
            new AcceptOwnerContact(body.Token, userId, email, emailVerified, firstName, lastName), cancellationToken));
    }
}

/// <summary>The secret of the link of the invitation.</summary>
public sealed record AcceptOwnerContactBody(string? Token);
