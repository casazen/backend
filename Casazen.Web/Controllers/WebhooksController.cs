using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Web.BackgroundJobs;
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

            if (string.IsNullOrEmpty(webhookSecret))
            {
                _logger.LogError("Stripe webhook secret not configured");
                return StatusCode(500, "Webhook secret not configured");
            }

            // Verify webhook signature
            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Invalid Stripe webhook signature");
                return BadRequest("Invalid signature");
            }

            _logger.LogInformation("Received Stripe webhook: {EventType} ({EventId})", stripeEvent.Type, stripeEvent.Id);

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
            return StatusCode(500, "Internal server error");
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

            if (string.IsNullOrEmpty(webhookSecret))
            {
                _logger.LogError("Stripe Connect webhook secret not configured");
                return StatusCode(500, "Webhook secret not configured");
            }

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Invalid Stripe Connect webhook signature");
                return BadRequest("Invalid signature");
            }

            _logger.LogInformation(
                "Received Stripe Connect webhook: {EventType} ({EventId})",
                stripeEvent.Type,
                stripeEvent.Id);

            _backgroundJobClient.Enqueue<StripeWebhookJob>(job =>
                job.ProcessEventAsync(stripeEvent.Id, stripeEvent.Type, json, WebhookSource.Connected));

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe Connect webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Handles incoming OTA platform webhooks (Airbnb, Booking.com, etc.)
    /// Queues sync jobs for background processing
    /// </summary>
    /// <param name="platform">OTA platform name (airbnb, booking, expedia, etc.)</param>
    /// <returns>200 OK to acknowledge receipt</returns>
    [HttpPost("ota/{platform}")]
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
    /// Handles e-signature provider callbacks (signing completed/partially signed).
    /// Validates provider signature header and queues background processing.
    /// </summary>
    [HttpPost("esign")]
    public async Task<IActionResult> ESignWebhook()
    {
        try
        {
            var payload = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

            var webhookSecret = _configuration["ESign:WebhookSecret"];
            if (string.IsNullOrEmpty(webhookSecret))
            {
                _logger.LogError("ESign webhook secret not configured");
                return StatusCode(500, "Webhook secret not configured");
            }

            var signatureHeader = Request.Headers["X-ESign-Signature"].ToString();
            if (string.IsNullOrEmpty(signatureHeader))
            {
                _logger.LogWarning("ESign webhook received without signature header");
                return Unauthorized("Missing signature");
            }

            byte[] providedBytes;
            try { providedBytes = Convert.FromHexString(signatureHeader); }
            catch (FormatException)
            {
                _logger.LogWarning("ESign webhook signature header is not valid hex");
                return Unauthorized("Invalid signature");
            }

            var expectedBytes = HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(webhookSecret),
                System.Text.Encoding.UTF8.GetBytes(payload));

            if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            {
                _logger.LogWarning("Invalid e-sign webhook signature");
                return Unauthorized("Invalid signature");
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
}
