using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Dtos.ConcordatoFacile;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// ConcordatoFacile one-time payment and access management (ADR-005).
/// Endpoints require lease.read OR lease.create permission in the long-rent context.
/// </summary>
[ApiController]
[Route("api/leases/{leaseId:guid}/concordatofacile")]
[Authorize] // Base authentication; OR permission check is programmatic per ADR-005-R9
public class ConcordatoFacileController(
    IConcordatoFacileCheckoutService checkoutService,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// Creates a Stripe Checkout session for ConcordatoFacile one-time payment (EUR 119,00).
    /// Reuses an existing pending session for the same lease if not expired (ADR-005-R1, ADR-005-R2).
    /// Requires lease.read OR lease.create permission (ADR-005-R9).
    /// </summary>
    /// <param name="leaseId">The lease contract ID from the route</param>
    /// <param name="request">Empty request body (required for POST)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Checkout session ID and URL to redirect the user to Stripe</returns>
    [HttpPost("checkout")]
    [ProducesResponseType(typeof(CheckoutResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckoutResponse>> CreateCheckout(
        Guid leaseId,
        [FromBody] CreateCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // ADR-005-R9: Authorize with LeaseCreate OR LeaseRead policy
        var hasLeaseRead = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.LeaseRead)).Succeeded;
        var hasLeaseCreate = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.LeaseCreate)).Succeeded;

        if (!hasLeaseRead && !hasLeaseCreate)
            return Forbid();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "org_not_found", "NoOrganizationAssigned");

        var result = await checkoutService.CreateCheckoutSessionAsync(
            leaseId,
            orgId.Value,
            userId,
            cancellationToken);

        return Ok(new CheckoutResponse
        {
            SessionId = result.SessionId,
            SessionUrl = result.SessionUrl
        });
    }
}
