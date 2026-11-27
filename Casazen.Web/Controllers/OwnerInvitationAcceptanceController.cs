using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.OwnerInvitations;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The acceptance of an invitation to the owner portal (E10/F02/US02/T02, NF-109, ADR-011-R6/R10): the endpoint behind the page
/// <c>/portale-proprietari/invito</c>, which keeps the token through the Auth0 sign-in and calls it once the owner is signed in. The secret token is
/// in the body, never in a URL.
/// </summary>
/// <remarks>
/// <para><b>Who may call it.</b> Any signed-in account: the owner has no org, no role and no portal claim yet, so no policy can name it, and the
/// endpoint is in the allow list of the authentication-only actions. What decides is the secret token, the e-mail of the account (from the token or
/// from Auth0, <b>verified</b>, and equal to the invited address) and the state of the invitation. A token that is unknown, lapsed, withdrawn or used
/// by another account gives access to nothing.</para>
/// <para><b>What it grants.</b> Only the ownerships of the invitation that are not over, of the org that invited and of a property of that org
/// (<c>PropertyOwnership.OwnerUserId</c>); nothing of any other registry owner, org or property. A repeat by the same account answers the same.</para>
/// <para>Behind <c>Features:AgencyTier</c> like the rest of the agency owner portal: 404 while it is off. The answer is private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/owner-invitations")]
[FeatureGate(FeatureFlags.AgencyTier)]
[PrivateAnswer]
public sealed class OwnerInvitationAcceptanceController(
    IOwnerInvitationService invitations,
    IAccountEmailResolver accountEmails) : ControllerBase
{
    /// <summary>
    /// The signed-in account accepts the invitation and becomes the owner of the ownerships of the invitation. 403 <c>owner_invitation_email_not_verified</c>
    /// and <c>owner_invitation_email_mismatch</c> (the e-mail of the account is not the invited one); 410 <c>owner_invitation_invalid</c>,
    /// <c>owner_invitation_expired</c>, <c>owner_invitation_used</c>, <c>owner_invitation_revoked</c> when the link no longer works. Sending it twice
    /// finds it done and answers the same.
    /// </summary>
    [HttpPost("accept")]
    [Authorize(Policy = CasazenPolicies.Authenticated)]
    [ProducesResponseType(typeof(OwnerInvitationAcceptedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<ActionResult<OwnerInvitationAcceptedDto>> Accept(
        [FromBody] AcceptOwnerInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // The email and its verification come from the token or from Auth0, never from the body.
        var (email, emailVerified) = await accountEmails.ResolveAsync(User, userId, cancellationToken);
        var (_, firstName, lastName) = AccountEmailResolver.ReadProfile(User);

        var accepted = await invitations.AcceptAsync(
            new AcceptOwnerInvitation(request.Token, userId, email, emailVerified, firstName, lastName),
            cancellationToken);
        return Ok(OwnerInvitationAcceptedDto.From(accepted));
    }
}
