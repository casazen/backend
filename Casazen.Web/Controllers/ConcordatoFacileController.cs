using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
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
    IConcordatoFacileAccessService accessService,
    IConcordatoFacileGiftService giftService,
    IConcordatoFacileLinkService linkService,
    IConcordatoFacileStatusCacheService statusCacheService,
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

    /// <summary>
    /// Checks ConcordatoFacile access for a lease (ADR-005-R8).
    /// Returns access status, source (payment/gift/admin), and gift availability.
    /// Verifies server-side state only, never trusts client state or URL params.
    /// </summary>
    /// <param name="leaseId">The lease contract ID from the route</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Access result with source, gift availability, and timestamps</returns>
    [HttpGet("access")]
    [Authorize(Policy = CasazenPolicies.LeaseRead)]
    [ProducesResponseType(typeof(AccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AccessResponse>> GetAccess(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // ADR-005-R8: Authorize with LeaseRead policy
        var hasLeaseRead = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.LeaseRead)).Succeeded;

        if (!hasLeaseRead)
            return Forbid();

        var result = await accessService.CheckAccessAsync(leaseId, User, cancellationToken);

        var sourceString = result.Source switch
        {
            Core.Services.CfAccessSource.Payment => "payment",
            Core.Services.CfAccessSource.Gift => "gift",
            Core.Services.CfAccessSource.Admin => "admin",
            _ => null
        };

        return Ok(new AccessResponse
        {
            HasAccess = result.HasAccess,
            Source = sourceString,
            GiftAvailable = result.GiftAvailable,
            GiftConsumed = result.GiftConsumed,
            PurchasedAt = result.PurchasedAt,
            GiftAssignedAt = result.GiftAssignedAt
        });
    }

    /// <summary>
    /// Consumes a ConcordatoFacile gift for a lease (ADR-006-R4, ADR-006-R5).
    /// Uses PostgreSQL advisory locks for atomicity (ADR-006-R4).
    /// Re-entry for the same leaseId returns 200 OK (ADR-006-R6).
    /// Returns 409 conflict if gift already consumed for another lease.
    /// </summary>
    /// <param name="leaseId">The lease contract ID from the route</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Access status or 409 conflict</returns>
    [HttpPost("consume-gift")]
    [Authorize(Policy = CasazenPolicies.LeaseRead)]
    [ProducesResponseType(typeof(AccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AccessResponse>> ConsumeGift(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "org_not_found", "NoOrganizationAssigned");

        var result = await giftService.ConsumeGiftAsync(orgId.Value, leaseId, cancellationToken);

        if (result == CfGiftConsumeResult.AlreadyConsumedForOtherLease)
        {
            return this.ApiProblem(
                StatusCodes.Status409Conflict,
                "concordatofacile_gift_already_consumed",
                "GiftAlreadyConsumed");
        }

        // Both Success and AlreadyConsumedForThisLease return 200 OK with access state
        var accessResult = await accessService.CheckAccessAsync(leaseId, User, cancellationToken);

        var sourceString = accessResult.Source switch
        {
            Core.Services.CfAccessSource.Payment => "payment",
            Core.Services.CfAccessSource.Gift => "gift",
            Core.Services.CfAccessSource.Admin => "admin",
            _ => null
        };

        return Ok(new AccessResponse
        {
            HasAccess = accessResult.HasAccess,
            Source = sourceString,
            GiftAvailable = accessResult.GiftAvailable,
            GiftConsumed = accessResult.GiftConsumed,
            PurchasedAt = accessResult.PurchasedAt,
            GiftAssignedAt = accessResult.GiftAssignedAt
        });
    }

    /// <summary>
    /// Generates a signed link to the ConcordatoFacile partner portal (ADR-007).
    /// Link expires in 15 minutes and includes HMAC-SHA256 signature.
    /// Requires LeaseCreate policy (write-level action) and verified access.
    /// </summary>
    /// <param name="leaseId">The lease contract ID from the route</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Signed URL to open in a new tab with Cache-Control: private, no-store header</returns>
    [HttpPost("link")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [ProducesResponseType(typeof(LinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<LinkResponse>> GenerateLink(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        try
        {
            var result = await linkService.GenerateSignedLinkAsync(leaseId, User, cancellationToken);

            // ADR-007-R11: Set Cache-Control header to prevent caching of temporary signed URL
            Response.Headers.CacheControl = "private, no-store";

            return Ok(new LinkResponse { Url = result.Url });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Core.Exceptions.DomainRuleException ex) when (ex.Code == "property_municipality_unknown")
        {
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, ex.Code, ex.MessageKey);
        }
    }

    /// <summary>
    /// Gets cached practice status for a lease (ADR-008-R15).
    /// Returns practices and timing metadata for frontend polling.
    /// Requires LeaseRead policy (ADR-008-R11).
    /// </summary>
    /// <param name="leaseId">The lease contract ID from the route</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Status response with practices, timing, and stale flag</returns>
    [HttpGet("status")]
    [Authorize(Policy = CasazenPolicies.LeaseRead)]
    [ProducesResponseType(typeof(StatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StatusResponse>> GetStatus(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "org_not_found", "NoOrganizationAssigned");

        // ADR-008-R11: Authorize with LeaseRead policy before calling CF API
        var result = await statusCacheService.GetPracticesAsync(leaseId, orgId.Value, cancellationToken);

        // ADR-008-R12: Cache-Control header to prevent caching of status data
        Response.Headers.CacheControl = "private, no-store";

        return Ok(new StatusResponse
        {
            Practices = result.Practices.Select(p => new PracticeResponse
            {
                PracticeId = p.PracticeId,
                Status = p.Status,
                StatusChangedAt = p.StatusChangedAt,
                SubmittedAt = p.SubmittedAt,
                Comune = p.Comune,
                ComuneDiffersFromLink = p.ComuneDiffersFromLink,
                CloseReason = p.CloseReason
            }).ToList(),
            LastCheckedAt = result.LastCheckedAt,
            NextUpdateAt = result.NextUpdateAt,
            Stale = result.IsStale
        });
    }
}
