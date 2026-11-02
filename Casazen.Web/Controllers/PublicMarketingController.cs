using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public (no-auth) marketing consent double opt-in and unsubscribe endpoints (NF-05, ADR-007-R17, R18, R21, R22).
/// Verifies HMAC-signed confirmation and unsubscribe links. The logs never carry the e-mail address (DEB-01).
/// </summary>
[ApiController]
[Route("api/public/marketing")]
[AllowAnonymous]
public class PublicMarketingController(
    AppDbContext db,
    MarketingUnsubscribeLinks unsubscribeLinks,
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
            logger.LogWarning("Invalid HMAC signature for marketing email confirmation: guestId={GuestId}", guestId);
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
            logger.LogInformation("Marketing email already confirmed: guestId={GuestId}", guestId);
            return Ok(new { confirmed = true, alreadyConfirmed = true });
        }

        // ADR-007-R21: Mark as confirmed
        consentRecord.EmailConfirmed = true;
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        logger.LogInformation("Marketing email confirmed via double opt-in: guestId={GuestId}", guestId);

        return Ok(new { confirmed = true, alreadyConfirmed = false });
    }

    /// <summary>
    /// One-click unsubscribe via HMAC-signed link (NF-05, ADR-007-R18, R22).
    /// ADR-007-R18: HMAC-SHA256 of emailHash + "." + expiresAt, 30-day expiration, checked by <see cref="MarketingUnsubscribeLinks"/>,
    /// which also builds the links of the campaigns (DEB-01). It is the target of their <c>List-Unsubscribe</c> header too: a mail client
    /// posts <c>List-Unsubscribe=One-Click</c> here (RFC 8058), a body this action does not read.
    /// ADR-007-R22: Adds to SuppressionListEntry on valid signature.
    /// </summary>
    /// <param name="emailHash">SHA-256 hash of the normalized email address</param>
    /// <param name="expiresAt">Link expiration timestamp in format yyyy-MM-ddTHH:mm:ssZ</param>
    /// <param name="signature">HMAC-SHA256 signature (hex lowercase)</param>
    /// <param name="orgId">Organization ID (required to identify the tenant)</param>
    [HttpPost("unsubscribe")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<IActionResult> Unsubscribe(
        [FromQuery] string? emailHash,
        [FromQuery] string? expiresAt,
        [FromQuery] string? signature,
        [FromQuery] Guid orgId)
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(emailHash))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "email_hash_missing", "EmailHashMissing");

        if (string.IsNullOrWhiteSpace(expiresAt))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "expires_at_missing", "ExpiresAtMissing");

        if (string.IsNullOrWhiteSpace(signature))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "signature_missing", "SignatureMissing");

        if (orgId == Guid.Empty)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "org_id_missing", "OrgIdMissing");

        // Expiry and HMAC signature (ADR-007-R18)
        switch (unsubscribeLinks.Verify(emailHash, expiresAt, signature))
        {
            case MarketingUnsubscribeCheck.Valid:
                break;
            case MarketingUnsubscribeCheck.InvalidTimestamp:
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "invalid_timestamp", "InvalidTimestamp");
            case MarketingUnsubscribeCheck.Expired:
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "link_expired", "LinkExpired");
            case MarketingUnsubscribeCheck.NotConfigured:
                logger.LogError("{Variable} is not configured: unsubscribe links cannot be checked", MarketingUnsubscribeLinks.SecretVariable);
                return this.ApiProblem(StatusCodes.Status500InternalServerError, "configuration_error", "ConfigurationError");
            case MarketingUnsubscribeCheck.InvalidSignature:
                logger.LogWarning(
                    "Invalid HMAC signature for unsubscribe: orgId={OrgId}, emailHash={EmailHash}",
                    orgId, emailHash);
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "invalid_signature", "InvalidSignature");
        }

        // ADR-007-R22: Add to suppression list
        // Note: We cannot reconstruct the original email from the hash, so we store the hash directly.
        // The suppression service expects an email, but in this case we're working with a pre-computed hash.
        // We'll add the hash directly to the suppression list.
        var entry = new Core.Entities.SuppressionListEntry
        {
            OrgId = orgId,
            EmailHash = emailHash,
            Source = "unsubscribe",
            AddedAt = DateTime.UtcNow
        };

        // Check if already suppressed (idempotent)
        var existing = await db.SuppressionListEntries
            .Where(e => e.OrgId == orgId && e.EmailHash == emailHash)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        if (existing is null)
        {
            db.SuppressionListEntries.Add(entry);
            await db.SaveChangesAsync(HttpContext.RequestAborted);

            logger.LogInformation(
                "Email hash added to suppression list via one-click unsubscribe: orgId={OrgId}, emailHash={EmailHash}",
                orgId, emailHash);
        }
        else
        {
            logger.LogInformation(
                "Email hash already suppressed: orgId={OrgId}, emailHash={EmailHash}",
                orgId, emailHash);
        }

        return Ok(new { unsubscribed = true });
    }
}
