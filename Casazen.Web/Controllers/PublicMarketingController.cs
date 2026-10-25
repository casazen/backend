using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public (no-auth) marketing consent double opt-in endpoint (NF-05, ADR-007-R17, R21).
/// Verifies HMAC-signed confirmation links sent to guest marketing emails.
/// </summary>
[ApiController]
[Route("api/public/marketing")]
[AllowAnonymous]
public class PublicMarketingController(
    AppDbContext db,
    ILogger<PublicMarketingController> logger) : ControllerBase
{
    /// <summary>
    /// Confirms a guest's marketing email via HMAC-signed link (double opt-in).
    /// ADR-007-R17: HMAC-SHA256 of guestId.toLowerCase() + "." + marketingEmail.toLowerCase() + "." + expiresAt
    /// ADR-007-R21: Sets MarketingConsentRecord.EmailConfirmed = true when signature is valid.
    /// </summary>
    /// <param name="guestId">Guest ID from the confirmation link</param>
    /// <param name="email">Marketing email address (must match the record)</param>
    /// <param name="expiresAt">Link expiration timestamp in format yyyy-MM-ddTHH:mm:ssZ</param>
    /// <param name="signature">HMAC-SHA256 signature (hex lowercase)</param>
    [HttpPost("confirm-email")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<IActionResult> ConfirmEmail(
        [FromQuery] Guid guestId,
        [FromQuery] string? email,
        [FromQuery] string? expiresAt,
        [FromQuery] string? signature)
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(email))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "email_missing", "EmailMissing");

        if (string.IsNullOrWhiteSpace(expiresAt))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "expires_at_missing", "ExpiresAtMissing");

        if (string.IsNullOrWhiteSpace(signature))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "signature_missing", "SignatureMissing");

        // Check link expiration
        if (!DateTime.TryParseExact(expiresAt, "yyyy-MM-ddTHH:mm:ssZ",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var expirationTime))
        {
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "invalid_timestamp", "InvalidTimestamp");
        }

        if (DateTime.UtcNow > expirationTime)
        {
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "link_expired", "LinkExpired");
        }

        // Verify HMAC signature (ADR-007-R17)
        var secret = Environment.GetEnvironmentVariable("SIGNED_LINK_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogError("SIGNED_LINK_SECRET environment variable is not configured");
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "configuration_error", "ConfigurationError");
        }

        var payload = $"{guestId.ToString().ToLowerInvariant()}.{email.ToLowerInvariant()}.{expiresAt}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(secretBytes, payloadBytes);
        var expectedSignature = Convert.ToHexString(hash).ToLowerInvariant();

        if (signature.ToLowerInvariant() != expectedSignature)
        {
            logger.LogWarning(
                "Invalid HMAC signature for marketing email confirmation: guestId={GuestId}, email={Email}",
                guestId, email);
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "invalid_signature", "InvalidSignature");
        }

        // Find the marketing consent record
        var consentRecord = await db.MarketingConsentRecords
            .Where(r => r.GuestId == guestId && r.MarketingEmail.ToLower() == email.ToLower())
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        if (consentRecord is null)
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, "consent_record_not_found", "ConsentRecordNotFound");
        }

        // Already confirmed: idempotent success
        if (consentRecord.EmailConfirmed)
        {
            logger.LogInformation(
                "Marketing email already confirmed: guestId={GuestId}, email={Email}",
                guestId, email);
            return Ok(new { confirmed = true, alreadyConfirmed = true });
        }

        // ADR-007-R21: Mark as confirmed
        consentRecord.EmailConfirmed = true;
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        logger.LogInformation(
            "Marketing email confirmed via double opt-in: guestId={GuestId}, email={Email}",
            guestId, email);

        return Ok(new { confirmed = true, alreadyConfirmed = false });
    }
}
