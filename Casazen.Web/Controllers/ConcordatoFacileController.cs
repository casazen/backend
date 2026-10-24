using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Dtos.ConcordatoFacile;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// ConcordatoFacile one-time payment and access management (ADR-005).
/// Endpoints require lease.read OR lease.create permission in the long-rent context.
/// The whole controller, the practice lookup of the upload page included, is behind <c>Features:ConcordatoFacile</c> (CF-FIX): with
/// the flag off every action answers 404 before authentication. The Stripe webhooks that record a payment already made are not.
/// </summary>
[ApiController]
[Route("api/leases/{leaseId:guid}/concordatofacile")]
[FeatureGate(FeatureFlags.ConcordatoFacile)]
[Authorize] // Base authentication; OR permission check is programmatic per ADR-005-R9
public class ConcordatoFacileController(
    IConcordatoFacileCheckoutService checkoutService,
    IConcordatoFacileAccessService accessService,
    IConcordatoFacileGiftService giftService,
    IConcordatoFacileLinkService linkService,
    IConcordatoFacileStatusCacheService statusCacheService,
    ConcordatoFacileApiClient apiClient,
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// Creates a Stripe Checkout session for ConcordatoFacile one-time payment (EUR 119,00).
    /// Reuses an existing pending session for the same lease if not expired (ADR-005-R1, ADR-005-R2).
    /// Requires lease.read OR lease.create permission (ADR-005-R9).
    /// 404 <c>lease_not_found</c> when the lease does not exist or belongs to another org; 409
    /// <c>concordatofacile_already_purchased</c> when the lease already has the access (payment or consumed gift) or has already
    /// paid and the webhook has not arrived yet (the payment is recorded on the spot): a second session is never created.
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
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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

    /// <summary>
    /// Looks up which lease a practice belongs to (NF-06).
    /// Used when returning from the ConcordatoFacile partner portal with a practice parameter.
    /// Validates practice ID format and verifies tenant isolation.
    /// </summary>
    /// <param name="practiceId">ConcordatoFacile practice identifier (format: P-[0-9A-Fa-f]{16})</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Lease lookup result or 404 if practice not found or not accessible</returns>
    [HttpGet("/api/concordatofacile/practices/{practiceId}/lease")]
    [Authorize(Policy = CasazenPolicies.LeaseRead)]
    [ProducesResponseType(typeof(PracticeLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PracticeLookupResponse>> LookupPractice(
        string practiceId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // Validate practiceId format: ^P-[0-9A-Fa-f]{16}$ (case-insensitive)
        if (!System.Text.RegularExpressions.Regex.IsMatch(practiceId, @"^P-[0-9A-Fa-f]{16}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, "practice_not_found", "PracticeNotFound");
        }

        // Get practice from CF API
        var practice = await apiClient.GetPracticeAsync(practiceId, cancellationToken);

        if (practice is null)
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, "practice_not_found", "PracticeNotFound");
        }

        // If leaseId is null, return manualSelectionRequired
        if (practice.LeaseId is null)
        {
            return Ok(new PracticeLookupResponse
            {
                LeaseId = null,
                PracticeId = practice.PracticeId,
                Status = null,
                ManualSelectionRequired = true
            });
        }

        // Verify tenant isolation: LeaseContract.OrgId must match user's OrgId
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, "practice_not_found", "PracticeNotFound");
        }

        var lease = await db.LeaseContracts
            .Where(l => l.Id == practice.LeaseId.Value)
            .Select(l => new { l.OrgId })
            .FirstOrDefaultAsync(cancellationToken);

        if (lease is null || lease.OrgId != orgId.Value)
        {
            // Don't reveal existence - return 404
            return this.ApiProblem(StatusCodes.Status404NotFound, "practice_not_found", "PracticeNotFound");
        }

        // Authorized - return lease info with status
        return Ok(new PracticeLookupResponse
        {
            LeaseId = practice.LeaseId.Value.ToString(),
            PracticeId = practice.PracticeId,
            Status = practice.Status,
            ManualSelectionRequired = false
        });
    }
}
