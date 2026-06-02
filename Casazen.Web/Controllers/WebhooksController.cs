using System.Security.Cryptography;
using Casazen.Core.Services;
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

    public WebhooksController(
        IBackgroundJobClient backgroundJobClient,
        IConfiguration configuration,
        ILogger<WebhooksController> logger,
        ILeaseWorkflowService leaseWorkflowService)
    {
        _backgroundJobClient = backgroundJobClient;
        _configuration = configuration;
        _logger = logger;
        _leaseWorkflowService = leaseWorkflowService;
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
                job.ProcessEventAsync(stripeEvent.Id, stripeEvent.Type, json));

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
    /// Handles incoming OTA platform webhooks (Airbnb, Booking.com, etc.)
    /// Queues sync jobs for background processing
    /// </summary>
    /// <param name="platform">OTA platform name (airbnb, booking, expedia, etc.)</param>
    /// <returns>200 OK to acknowledge receipt</returns>
    [HttpPost("ota/{platform}")]
    public IActionResult OtaWebhook(string platform)
    {
        try
        {
            _logger.LogInformation("Received {Platform} webhook", platform);

            // Extract data from request (platform-specific)
            // For now, we'll use a simple property ID from query string
            // In production, parse platform-specific webhook payload
            var propertyId = Request.Query["propertyId"].ToString();
            if (string.IsNullOrEmpty(propertyId) || !Guid.TryParse(propertyId, out var propertyGuid))
            {
                _logger.LogWarning("{Platform} webhook missing or invalid propertyId", platform);
                return BadRequest("Invalid or missing propertyId");
            }

            // Queue sync job for background processing
            _backgroundJobClient.Enqueue<OtaSyncJob>(job =>
                job.ExecuteAsync(propertyGuid));

            _logger.LogInformation("Queued {Platform} sync for property {PropertyId}", platform, propertyId);

            // Return 200 immediately to acknowledge receipt
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
