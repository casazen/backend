using Casazen.Infrastructure.External;
using Stripe;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Background job for processing Stripe webhook events asynchronously
/// Allows webhook endpoint to respond within 3-second timeout while processing happens in background
/// </summary>
public class StripeWebhookJob
{
    private readonly StripeWebhookHandler _webhookHandler;
    private readonly ILogger<StripeWebhookJob> _logger;

    public StripeWebhookJob(StripeWebhookHandler webhookHandler, ILogger<StripeWebhookJob> logger)
    {
        _webhookHandler = webhookHandler;
        _logger = logger;
    }

    /// <summary>
    /// Processes a Stripe webhook event
    /// </summary>
    /// <param name="eventId">Stripe event ID</param>
    /// <param name="eventType">Stripe event type (e.g., "payment_intent.succeeded")</param>
    /// <param name="eventJson">JSON representation of the Stripe event</param>
    public async Task ProcessEventAsync(string eventId, string eventType, string eventJson)
    {
        try
        {
            _logger.LogInformation("Processing Stripe webhook event {EventId} of type {EventType}", eventId, eventType);

            // Deserialize the Stripe event
            var stripeEvent = Event.FromJson(eventJson);

            // Process the event through the webhook handler
            await _webhookHandler.HandleEventAsync(stripeEvent);

            _logger.LogInformation("Successfully processed Stripe webhook event {EventId}", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook event {EventId}", eventId);
            throw; // Re-throw to let Hangfire handle retry logic
        }
    }
}
