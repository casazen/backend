using Casazen.Core.Entities.Enums;
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
    /// <param name="source">Webhook ingress source (platform vs connected account).</param>
    public async Task ProcessEventAsync(string eventId, string eventType, string eventJson, WebhookSource source)
    {
        try
        {
            _logger.LogInformation(
                "Processing Stripe webhook event {EventId} of type {EventType} from {Source}",
                eventId,
                eventType,
                source);

            var stripeEvent = Event.FromJson(eventJson);
            await _webhookHandler.HandleEventAsync(stripeEvent, source);

            _logger.LogInformation("Successfully processed Stripe webhook event {EventId}", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook event {EventId}", eventId);
            throw;
        }
    }

    /// <summary>
    /// Backward-compatible overload for callers that do not pass a webhook source.
    /// </summary>
    public Task ProcessEventAsync(string eventId, string eventType, string eventJson) =>
        ProcessEventAsync(eventId, eventType, eventJson, WebhookSource.Platform);
}
