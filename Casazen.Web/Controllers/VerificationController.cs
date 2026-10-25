using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Verification;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Operator verification queue for manual biometric verification (NF-12, ADR-013). Allows hosts to review
/// and decide on bookings where biometric verification escalated, failed after 3 attempts, or was declined.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.VerificationDecision)]
public class VerificationController(
    IVerificationQueueService verificationService,
    IHostScopeResolver hostScopeResolver,
    IOrgContextResolver orgContextResolver,
    ILogger<VerificationController> logger) : ControllerBase
{
    /// <summary>
    /// Get bookings pending manual verification within the caller's scope (AC-NF12-3).
    /// Returns bookings where BiometricOutcome is Escalate or BiometricAttempts >= 3,
    /// ordered by oldest check-in first.
    /// </summary>
    [HttpGet("pending")]
    [ProducesResponseType(typeof(IReadOnlyList<PendingVerificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<PendingVerificationDto>>> GetPending()
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var scope = await ResolveScopeAsync(userId);
        if (scope == null)
            return Unauthorized();

        var pending = await verificationService.GetPendingVerificationsAsync(userId, scope);

        return Ok(pending.Select(p => new PendingVerificationDto
        {
            BookingId = p.BookingId,
            PropertyId = p.PropertyId,
            PropertyName = p.PropertyName,
            CheckInDate = p.CheckInDate,
            CheckOutDate = p.CheckOutDate,
            GuestName = p.GuestName,
            GuestEmail = p.GuestEmail,
            BiometricOutcome = p.BiometricOutcome?.ToString(),
            BiometricAttempts = p.BiometricAttempts,
            RaisedAt = p.RaisedAt,
        }).ToList());
    }

    /// <summary>
    /// Request a new selfie photo from the guest (AC-NF12-5).
    /// Generates a signed link and queues an email to the guest.
    /// Returns 404 when the booking is outside the caller scope, has no active session or the guest has no email.
    /// </summary>
    [HttpPost("{bookingId:guid}/request-photo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RequestNewPhoto(Guid bookingId)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var scope = await ResolveScopeAsync(userId);
        if (scope == null)
            return Unauthorized();

        var success = await verificationService.RequestNewPhotoAsync(bookingId, scope);

        if (!success)
            return this.ApiProblem(StatusCodes.Status404NotFound, "no_session_or_email", "VerificationNoSessionOrEmail");

        logger.LogInformation("User {UserId} requested new photo for booking {BookingId}", userId, bookingId);
        return Ok();
    }

    /// <summary>
    /// Record manual verification decision (AC-NF12-6).
    /// <paramref name="request.Outcome"/> is true for acceptance, false for rejection.
    /// <paramref name="request.Note"/> is required for rejection, optional for acceptance.
    /// Returns 404 when the booking is outside the caller scope or has no pending verification.
    /// </summary>
    [HttpPost("{bookingId:guid}/verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecordVerification(Guid bookingId, [FromBody] VerificationDecisionRequest request)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var scope = await ResolveScopeAsync(userId);
        if (scope == null)
            return Unauthorized();

        if (!request.Outcome && string.IsNullOrWhiteSpace(request.Note))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "note_required", "VerificationNoteRequired");

        try
        {
            var success = await verificationService.RecordManualVerificationAsync(
                bookingId,
                scope,
                request.Outcome,
                request.Note);

            if (!success)
                return this.ApiProblem(StatusCodes.Status404NotFound, "no_pending_verification", "VerificationNoPending");

            logger.LogInformation(
                "User {UserId} recorded verification for booking {BookingId}: outcome={Outcome}",
                userId, bookingId, request.Outcome);

            return Ok();
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid verification request for booking {BookingId}", bookingId);
            return this.ApiProblem(StatusCodes.Status400BadRequest, "invalid_request", "VerificationInvalidRequest");
        }
    }

    /// <summary>
    /// Reject the guest's check-in (AC-NF12-7).
    /// Marks the verification as rejected with <paramref name="request.Reason"/> and prevents further attempts.
    /// Returns 404 when the booking is outside the caller scope or has no pending verification.
    /// </summary>
    [HttpPost("{bookingId:guid}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RejectVerification(Guid bookingId, [FromBody] VerificationRejectionRequest request)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var scope = await ResolveScopeAsync(userId);
        if (scope == null)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Reason))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "reason_required", "VerificationReasonRequired");

        try
        {
            var success = await verificationService.RejectVerificationAsync(bookingId, scope, request.Reason);

            if (!success)
                return this.ApiProblem(StatusCodes.Status404NotFound, "no_pending_verification", "VerificationNoPending");

            logger.LogInformation(
                "User {UserId} rejected verification for booking {BookingId}: reason={Reason}",
                userId, bookingId, request.Reason);

            return Ok();
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid verification request for booking {BookingId}", bookingId);
            return this.ApiProblem(StatusCodes.Status400BadRequest, "invalid_request", "VerificationInvalidRequest");
        }
    }

    /// <summary>The caller reach on host data (TN-3), null when the caller has no org.</summary>
    private async Task<HostScope?> ResolveScopeAsync(string userId)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync();
        return orgId == null ? null : await hostScopeResolver.ResolveAsync(userId, GetJwtRoles(), orgId.Value);
    }

    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private IReadOnlySet<string> GetJwtRoles() => User.GetRoles();
}
