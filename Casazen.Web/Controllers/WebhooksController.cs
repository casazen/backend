using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("webhooks")]
[AllowAnonymous] // Webhooks come from external services, not authenticated users
public class WebhooksController : ControllerBase
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebhooksController> _logger;

    private readonly ILeaseWorkflowService _leaseWorkflowService;
    private readonly IPropertyRepository _propertyRepository;

    public WebhooksController(
        IBackgroundJobClient backgroundJobClient,
        IConfiguration configuration,
        ILogger<WebhooksController> logger,
        ILeaseWorkflowService leaseWorkflowService,
        IPropertyRepository propertyRepository)
    {
        _backgroundJobClient = backgroundJobClient;
        _configuration = configuration;
        _logger = logger;
        _leaseWorkflowService = leaseWorkflowService;
        _propertyRepository = propertyRepository;
    }

    /// <summary>
    /// Handles incoming Stripe webhook events
    /// Validates signature and queues event for background processing
    /// </summary>
    /// <returns>200 OK to acknowledge receipt within 3-second timeout</returns>
    [HttpPost("stripe")]
    public async Task<IActionResult> StripeWebhook()
    {
        try
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var signatureHeader = Request.Headers["Stripe-Signature"].ToString();
            var webhookSecret = _configuration["Stripe:WebhookSecret"];

            // A placeholder committed in appsettings.json is public: signing with it would accept forged events.
            if (RequiredConfiguration.IsMissing(webhookSecret))
            {
                _logger.LogError("Stripe webhook secret not configured");
                return StripeWebhookNotConfigured();
            }

            // Verify webhook signature. throwOnApiVersionMismatch=false (A3-39): the Stripe dashboard endpoint can be
            // pinned to an API version other than the one this SDK build defaults to (Stripe.ApiVersion.Current); with
            // the default (true) that mismatch throws a StripeException indistinguishable, in the catch below, from a
            // forged signature, so every event is answered 400 forever and no booking is ever confirmed. The HMAC
            // signature is still verified in full either way — only the version check becomes non-fatal.
            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret, throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Invalid Stripe webhook signature");
                return StripeWebhookSignatureInvalid();
            }

            _logger.LogInformation(
                "Received Stripe webhook: {EventType} ({EventId}, api version {EventApiVersion})",
                stripeEvent.Type,
                stripeEvent.Id,
                stripeEvent.ApiVersion);

            // Queue the event for background processing
            // This allows us to respond within 3 seconds while processing happens asynchronously
            _backgroundJobClient.Enqueue<StripeWebhookJob>(job =>
                job.ProcessEventAsync(stripeEvent.Id, stripeEvent.Type, json, WebhookSource.Platform));

            _logger.LogInformation("Queued Stripe webhook event {EventId} for background processing", stripeEvent.Id);

            // Return 200 immediately to acknowledge receipt
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook");
            return StripeWebhookFailed();
        }
    }

    /// <summary>
    /// Connected-account Stripe webhooks (Connect onboarding, account.updated).
    /// Verified with <c>Stripe:ConnectWebhookSecret</c> per RF2.
    /// </summary>
    [HttpPost("stripe/connect")]
    public async Task<IActionResult> StripeConnectWebhook()
    {
        try
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var signatureHeader = Request.Headers["Stripe-Signature"].ToString();
            var webhookSecret = _configuration["Stripe:ConnectWebhookSecret"];

            // A placeholder committed in appsettings.json is public: signing with it would accept forged events.
            if (RequiredConfiguration.IsMissing(webhookSecret))
            {
                _logger.LogError("Stripe Connect webhook secret not configured");
                return StripeWebhookNotConfigured();
            }

            // See the platform endpoint above (A3-39): tolerant of an API version pinned differently on the connect
            // endpoint, still fully signature-verified.
            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret, throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Invalid Stripe Connect webhook signature");
                return StripeWebhookSignatureInvalid();
            }

            _logger.LogInformation(
                "Received Stripe Connect webhook: {EventType} ({EventId}, api version {EventApiVersion})",
                stripeEvent.Type,
                stripeEvent.Id,
                stripeEvent.ApiVersion);

            _backgroundJobClient.Enqueue<StripeWebhookJob>(job =>
                job.ProcessEventAsync(stripeEvent.Id, stripeEvent.Type, json, WebhookSource.Connected));

            _logger.LogInformation("Queued Stripe Connect webhook event {EventId} for background processing", stripeEvent.Id);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe Connect webhook");
            return StripeWebhookFailed();
        }
    }

    // Stripe retries a delivery answered with a non-2xx status: an event is acknowledged (200) only once queued.
    private ObjectResult StripeWebhookNotConfigured() =>
        this.ApiProblem(StatusCodes.Status500InternalServerError, "stripe_webhook_not_configured", "StripeWebhookNotConfigured");

    private ObjectResult StripeWebhookSignatureInvalid() =>
        this.ApiProblem(StatusCodes.Status400BadRequest, "invalid_signature", "StripeWebhookSignatureInvalid");

    private ObjectResult StripeWebhookFailed() =>
        this.ApiProblem(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError, "InternalServerErrorDetail");

    /// <summary>
    /// Handles incoming OTA platform webhooks (Airbnb, Booking.com, etc.)
    /// Queues sync jobs for background processing.
    /// OTA partner API in freeze (D10): 404 while <see cref="FeatureFlags.OtaPartnerApi"/> is off.
    /// </summary>
    /// <param name="platform">OTA platform name (airbnb, booking, expedia, etc.)</param>
    /// <returns>200 OK to acknowledge receipt</returns>
    [HttpPost("ota/{platform}")]
    [FeatureGate(FeatureFlags.OtaPartnerApi)]
    public async Task<IActionResult> OtaWebhook(string platform)
    {
        try
        {
            var payload = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var webhookSecret = _configuration["OTA:WebhookSecret"];
            if (string.IsNullOrEmpty(webhookSecret))
            {
                _logger.LogError("OTA webhook secret not configured");
                return StatusCode(500, "Webhook secret not configured");
            }

            var signatureHeader = Request.Headers["X-OTA-Signature"].ToString();
            if (string.IsNullOrEmpty(signatureHeader))
            {
                _logger.LogWarning("{Platform} webhook received without signature header", platform);
                return Unauthorized("Missing signature");
            }

            byte[] providedBytes;
            try { providedBytes = Convert.FromHexString(signatureHeader); }
            catch (FormatException)
            {
                _logger.LogWarning("{Platform} webhook signature header is not valid hex", platform);
                return Unauthorized("Invalid signature");
            }

            var expectedBytes = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(webhookSecret),
                Encoding.UTF8.GetBytes(payload));

            if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            {
                _logger.LogWarning("Invalid {Platform} webhook signature", platform);
                return Unauthorized("Invalid signature");
            }

            _logger.LogInformation("Received {Platform} webhook", platform);

            var propertyId = Request.Query["propertyId"].ToString();
            if (string.IsNullOrEmpty(propertyId) || !Guid.TryParse(propertyId, out var propertyGuid))
            {
                _logger.LogWarning("{Platform} webhook missing or invalid propertyId", platform);
                return BadRequest("Invalid or missing propertyId");
            }

            var property = await _propertyRepository.GetByIdAsync(propertyGuid);
            if (property is null)
            {
                _logger.LogWarning("{Platform} webhook references unknown property {PropertyId}", platform, propertyId);
                return NotFound("Property not found");
            }

            _backgroundJobClient.Enqueue<OtaSyncJob>(job =>
                job.ExecuteAsync(propertyGuid));

            _logger.LogInformation("Queued {Platform} sync for property {PropertyId}", platform, propertyId);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing {Platform} webhook", platform);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// E-signature provider callbacks (LT-02, A7-20). Only with <c>Features:ESignProvider</c> on (404 otherwise, before
    /// anything is read). The body must be signed with <c>ESign:WebhookSecret</c> (HMAC-SHA256, hex in
    /// <c>X-ESign-Signature</c>): 401 otherwise. The event is applied by <see cref="ESignWebhookJob"/>, which moves only a
    /// lease whose signature is in progress.
    /// </summary>
    [HttpPost("esign")]
    [FeatureGate(FeatureFlags.ESignProvider)]
    public async Task<IActionResult> ESignWebhook()
    {
        try
        {
            var payload = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            // Validated at startup with the flag on (ESignOptionsValidator); a public placeholder never signs anything.
            var webhookSecret = _configuration["ESign:WebhookSecret"];
            if (ESignOptionsValidator.IsWebhookSecretMissing(webhookSecret))
            {
                _logger.LogError("ESign webhook secret not configured");
                return this.ApiProblem(
                    StatusCodes.Status500InternalServerError, LeaseSigningErrorCodes.WebhookNotConfigured, "ESignWebhookNotConfigured");
            }

            var signatureHeader = Request.Headers["X-ESign-Signature"].ToString();
            if (string.IsNullOrEmpty(signatureHeader))
            {
                _logger.LogWarning("ESign webhook received without signature header");
                return ESignSignatureInvalid();
            }

            byte[] providedBytes;
            try { providedBytes = Convert.FromHexString(signatureHeader); }
            catch (FormatException)
            {
                _logger.LogWarning("ESign webhook signature header is not valid hex");
                return ESignSignatureInvalid();
            }

            var expectedBytes = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(webhookSecret!),
                Encoding.UTF8.GetBytes(payload));

            if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            {
                _logger.LogWarning("Invalid e-sign webhook signature");
                return ESignSignatureInvalid();
            }

            _backgroundJobClient.Enqueue<ESignWebhookJob>(job =>
                job.ProcessEventAsync(payload));

            _logger.LogInformation("Queued e-sign webhook event for background processing");
            return Ok(new { received = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing e-sign webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    private ObjectResult ESignSignatureInvalid() =>
        this.ApiProblem(StatusCodes.Status401Unauthorized, "invalid_signature", "ESignWebhookSignatureInvalid");
}
