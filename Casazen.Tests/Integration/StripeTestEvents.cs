using Stripe;

namespace Casazen.Tests.Integration;

/// <summary>Stripe events as <c>Event.FromJson</c> would build them, for the webhook handler tests.</summary>
internal static class StripeTestEvents
{
    public static string NewEventId() => $"evt_test_{Guid.NewGuid():N}";

    public static Event Subscription(
        string type,
        string subscriptionId,
        string status,
        Guid orgId,
        string customerId,
        string priceId,
        string? eventId = null) => new()
        {
            Id = eventId ?? NewEventId(),
            Type = type,
            Data = new EventData
            {
                Object = new Stripe.Subscription
                {
                    Id = subscriptionId,
                    Status = status,
                    CustomerId = customerId,
                    Metadata = new Dictionary<string, string> { ["orgId"] = orgId.ToString() },
                    Items = new StripeList<SubscriptionItem>
                    {
                        Data =
                        [
                            new SubscriptionItem
                            {
                                Price = new Price { Id = priceId },
                                CurrentPeriodEnd = DateTime.UtcNow.AddDays(30),
                            },
                        ],
                    },
                },
            },
        };

    public static Event Invoice(
        string type,
        string subscriptionId,
        string customerId,
        string billingReason,
        string? eventId = null) => new()
        {
            Id = eventId ?? NewEventId(),
            Type = type,
            Data = new EventData
            {
                Object = new Stripe.Invoice
                {
                    Id = $"in_test_{Guid.NewGuid():N}",
                    CustomerId = customerId,
                    Metadata = new Dictionary<string, string>(),
                    BillingReason = billingReason,
                    Subtotal = 2900,
                    Total = 2900,
                    Parent = new InvoiceParent
                    {
                        SubscriptionDetails = new InvoiceParentSubscriptionDetails { SubscriptionId = subscriptionId },
                    },
                },
            },
        };

    public static Event PaymentIntentSucceeded(string paymentIntentId, string? kind, string? eventId = null) => new()
    {
        Id = eventId ?? NewEventId(),
        Type = "payment_intent.succeeded",
        Data = new EventData
        {
            Object = new PaymentIntent
            {
                Id = paymentIntentId,
                Metadata = kind is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["kind"] = kind },
            },
        },
    };
}
