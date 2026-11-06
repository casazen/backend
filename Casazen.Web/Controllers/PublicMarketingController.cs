using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
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
/// Verifies HMAC-signed confirmation and unsubscribe links. The logs never carry the e-mail address (DEB-01) nor the hash that stands for it
/// (E07/F06/US01/T02). <b>Not behind <c>Features:CrmMarketing</c></b>: a link already sent has to keep working whatever the flag says.
/// </summary>
[ApiController]
[Route("api/public/marketing")]
[AllowAnonymous]
public class PublicMarketingController(
    AppDbContext db,
    MarketingUnsubscribeLinks unsubscribeLinks,
    ISuppressionService suppressionService,
    IMarketingConfirmationService confirmation,
    ILogger<PublicMarketingController> logger) : ControllerBase
{
    /// <summary>
    /// Confirms a marketing consent by the signed link of the double opt-in e-mail (E07/F04/US01/T01, NF-90, AC-NF90-5): the page
    /// <c>/marketing/confirm</c> reads the values from the fragment of its address and posts them here unchanged. The link names the contact
    /// (<c>contactId</c>), not the address, and the org is the contact's. HMAC-SHA256 of <c>contactId.expiresAt</c> with the secret of the
    /// signed links, checked in constant time by <see cref="IMarketingConfirmationService.ConfirmAsync"/>. 200 <c>{ confirmed: true,
    /// alreadyConfirmed }</c> (a second click changes nothing); 400 <c>contact_id_missing</c>, <c>expires_at_missing</c>,
    /// <c>signature_missing</c>; 422 <c>invalid_timestamp</c>, <c>link_expired</c>, <c>invalid_signature</c>; 404 <c>contact_not_found</c> only
    /// after a valid signature; 409 <c>contact_suppressed</c> (the address opted out: nothing is confirmed and the opt-out stands); 429.
    /// The old <c>confirm-email</c> endpoint stays as it is for links already out (none was ever sent).
    /// </summary>
    /// <param name="contactId">The contact the link was made for.</param>
    /// <param name="expiresAt">Link expiration timestamp in format yyyy-MM-ddTHH:mm:ssZ</param>
    /// <param name="signature">HMAC-SHA256 signature (hex lowercase)</param>
    [HttpPost("confirm")]
    [EnableRateLimiting(RateLimitPolicies.PublicMarketingConsent)]
    public async Task<IActionResult> Confirm(
        [FromQuery] Guid contactId,
        [FromQuery] string? expiresAt,
        [FromQuery] string? signature)
    {
        if (contactId == Guid.Empty)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "contact_id_missing", "ContactIdMissing");

        if (string.IsNullOrWhiteSpace(expiresAt))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "expires_at_missing", "ExpiresAtMissing");

        if (string.IsNullOrWhiteSpace(signature))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "signature_missing", "SignatureMissing");

        switch (await confirmation.ConfirmAsync(contactId, expiresAt, signature, HttpContext.RequestAborted))
        {
            case MarketingConfirmationOutcome.Confirmed:
                return Ok(new { confirmed = true, alreadyConfirmed = false });
            case MarketingConfirmationOutcome.AlreadyConfirmed:
                return Ok(new { confirmed = true, alreadyConfirmed = true });
            case MarketingConfirmationOutcome.InvalidTimestamp:
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "invalid_timestamp", "InvalidTimestamp");
            case MarketingConfirmationOutcome.LinkExpired:
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "link_expired", "LinkExpired");
            case MarketingConfirmationOutcome.NotConfigured:
                logger.LogError("{Variable} is not configured: confirmation links cannot be checked", MarketingConfirmationLinks.SecretVariable);
                return this.ApiProblem(StatusCodes.Status500InternalServerError, "configuration_error", "ConfigurationError");
            case MarketingConfirmationOutcome.ContactNotFound:
                return this.ApiProblem(StatusCodes.Status404NotFound, "contact_not_found", "ContactNotFound");
            case MarketingConfirmationOutcome.ContactSuppressed:
                return this.ApiProblem(StatusCodes.Status409Conflict, "contact_suppressed", "MarketingContactSuppressed");
            default:
                logger.LogWarning("Invalid HMAC signature for marketing confirmation: contactId={ContactId}", contactId);
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "invalid_signature", "InvalidSignature");
        }
    }

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
    /// ADR-007-R22: Adds to SuppressionListEntry on valid signature, through <see cref="ISuppressionService.AddByHashAsync"/> (idempotent; it also
    /// flags the CRM contacts with the same hash).
    /// </summary>
    /// <param name="emailHash">Keyed hash (HMAC-SHA256) of the normalized email address; the plain SHA-256 for a link issued before E07/F06/US01/T02</param>
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
                logger.LogWarning("Invalid HMAC signature for unsubscribe: orgId={OrgId}", orgId);
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "invalid_signature", "InvalidSignature");
        }

        // ADR-007-R22: Add to suppression list. The address cannot be rebuilt from the hash, so the service stores the hash the link carries
        // (keyed for a link issued since E07/F06/US01/T02, the plain SHA-256 for an older one), once, and flags the contacts with the same hash.
        // It logs the org and the source only.
        await suppressionService.AddByHashAsync(orgId, emailHash, "unsubscribe", HttpContext.RequestAborted);

        return Ok(new { unsubscribed = true });
    }
}
