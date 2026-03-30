## User Story

As a **property owner**, I want **automatic payment status updates** from Stripe webhooks, so that **payments are marked as completed/failed in real-time without manual reconciliation**.

## Context

`StripeWebhookHandler.cs` exists but implementation is incomplete. Critical for payment flow reliability.

## Technical Details

### Current Issue

The webhook handler exists but needs:
1. Signature verification (prevent spoofing)
2. Event handling for all Stripe event types
3. Idempotency (prevent duplicate processing)
4. Integration with PaymentService
5. Error handling and logging

### Files to Modify/Create

1. **Casazen.Infrastructure/External/StripeWebhookHandler.cs** (enhance existing)

```csharp
public class StripeWebhookHandler
{
    private readonly IPaymentService _paymentService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<StripeWebhookHandler> _logger;
    private readonly string _webhookSecret;

    public StripeWebhookHandler(
        IPaymentService paymentService,
        INotificationService notificationService,
        ILogger<StripeWebhookHandler> logger,
        IConfiguration configuration)
    {
        _paymentService = paymentService;
        _notificationService = notificationService;
        _logger = logger;
        _webhookSecret = configuration["Stripe:WebhookSecret"];
    }

    public bool VerifySignature(IHeaderDictionary headers, string payload)
    {
        try
        {
            var signature = headers["Stripe-Signature"].FirstOrDefault();
            if (string.IsNullOrEmpty(signature))
            {
                _logger.LogWarning("Missing Stripe-Signature header");
                return false;
            }

            // Stripe SDK will throw exception if signature invalid
            var stripeEvent = EventUtility.ConstructEvent(
                payload,
                signature,
                _webhookSecret
            );

            return true;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Invalid Stripe webhook signature");
            return false;
        }
    }

    public async Task HandleEventAsync(Event stripeEvent)
    {
        _logger.LogInformation("Processing Stripe event {EventId} of type {EventType}",
            stripeEvent.Id, stripeEvent.Type);

        // Idempotency check
        if (await _paymentService.IsEventProcessedAsync(stripeEvent.Id))
        {
            _logger.LogWarning("Event {EventId} already processed, skipping", stripeEvent.Id);
            return;
        }

        try
        {
            switch (stripeEvent.Type)
            {
                case Events.PaymentIntentSucceeded:
                    await HandlePaymentSucceeded(stripeEvent);
                    break;

                case Events.PaymentIntentPaymentFailed:
                    await HandlePaymentFailed(stripeEvent);
                    break;

                case Events.ChargeRefunded:
                    await HandleRefund(stripeEvent);
                    break;

                case Events.ChargeDisputeCreated:
                    await HandleDispute(stripeEvent);
                    break;

                default:
                    _logger.LogInformation("Unhandled event type {EventType}", stripeEvent.Type);
                    break;
            }

            // Mark event as processed
            await _paymentService.MarkEventProcessedAsync(stripeEvent.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Stripe event {EventId}", stripeEvent.Id);
            throw;
        }
    }

    private async Task HandlePaymentSucceeded(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        _logger.LogInformation("Payment succeeded for PaymentIntent {PaymentIntentId}", paymentIntent.Id);

        var payment = await _paymentService.GetByTransactionIdAsync(paymentIntent.Id);
        if (payment == null)
        {
            _logger.LogWarning("Payment not found for transaction {TransactionId}", paymentIntent.Id);
            return;
        }

        payment.Status = PaymentStatus.Completed;
        payment.UpdatedAt = DateTime.UtcNow;
        await _paymentService.UpdatePaymentAsync(payment);

        // Send confirmation email
        await _notificationService.SendPaymentReceiptAsync(payment.Id);

        _logger.LogInformation("Payment {PaymentId} marked as completed", payment.Id);
    }

    private async Task HandlePaymentFailed(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        _logger.LogWarning("Payment failed for PaymentIntent {PaymentIntentId}", paymentIntent.Id);

        var payment = await _paymentService.GetByTransactionIdAsync(paymentIntent.Id);
        if (payment == null)
            return;

        payment.Status = PaymentStatus.Failed;
        payment.UpdatedAt = DateTime.UtcNow;
        await _paymentService.UpdatePaymentAsync(payment);

        // Notify property owner
        _logger.LogInformation("Payment {PaymentId} marked as failed", payment.Id);
    }

    private async Task HandleRefund(Event stripeEvent)
    {
        var charge = stripeEvent.Data.Object as Charge;
        _logger.LogInformation("Refund processed for Charge {ChargeId}", charge.Id);

        var payment = await _paymentService.GetByTransactionIdAsync(charge.PaymentIntentId);
        if (payment == null)
            return;

        var refundAmount = charge.AmountRefunded / 100m; // Stripe amounts in cents

        if (refundAmount == payment.Amount)
        {
            payment.Status = PaymentStatus.Refunded;
        }
        else
        {
            payment.Status = PaymentStatus.PartiallyRefunded;
        }

        payment.UpdatedAt = DateTime.UtcNow;
        await _paymentService.UpdatePaymentAsync(payment);

        await _notificationService.SendRefundNotificationAsync(payment.Id);

        _logger.LogInformation("Payment {PaymentId} refund processed: {Amount}", payment.Id, refundAmount);
    }

    private async Task HandleDispute(Event stripeEvent)
    {
        var dispute = stripeEvent.Data.Object as Dispute;
        _logger.LogWarning("Dispute created for Charge {ChargeId}", dispute.ChargeId);

        var payment = await _paymentService.GetByTransactionIdAsync(dispute.PaymentIntentId);
        if (payment == null)
            return;

        // Alert property owner immediately
        _logger.LogWarning("DISPUTE: Payment {PaymentId}, Reason: {Reason}", payment.Id, dispute.Reason);
    }
}
```

2. **Add to IPaymentService**
```csharp
Task<Payment?> GetByTransactionIdAsync(string transactionId);
Task<bool> IsEventProcessedAsync(string eventId);
Task MarkEventProcessedAsync(string eventId);
```

3. **Create StripeEvent entity for idempotency**
```csharp
// Casazen.Core/Entities/StripeEvent.cs
[Table("StripeEvents")]
public class StripeEvent
{
    [Key, MaxLength(255)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
```

4. **Migration**
```bash
dotnet ef migrations add AddStripeEventIdempotency --project Casazen.Infrastructure
dotnet ef database update --project Casazen.Infrastructure
```

5. **Update WebhooksController**
```csharp
[HttpPost("stripe")]
public async Task<IActionResult> StripeWebhook()
{
    var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

    // Verify signature
    if (!_stripeWebhookHandler.VerifySignature(Request.Headers, json))
    {
        _logger.LogWarning("Invalid Stripe webhook signature");
        return Unauthorized();
    }

    var stripeEvent = EventUtility.ParseEvent(json);

    // Queue background job (from Issue #10)
    BackgroundJob.Enqueue<StripeWebhookJob>(
        job => job.ProcessEventAsync(stripeEvent)
    );

    return Ok();
}
```

## Acceptance Criteria

- [ ] Signature verification implemented and tested
- [ ] PaymentIntent.succeeded event updates payment to Completed
- [ ] PaymentIntent.payment_failed event updates payment to Failed
- [ ] Charge.refunded event updates payment to Refunded/PartiallyRefunded
- [ ] Charge.dispute.created logs warning and notifies owner
- [ ] Idempotency: duplicate events are not processed twice
- [ ] Confirmation email sent on successful payment
- [ ] Webhook responds < 1 second (with background job queuing)
- [ ] Integration test with Stripe CLI: `stripe listen --forward-to localhost:5001/webhooks/stripe`
- [ ] Unit tests for all event handlers

## Definition of Done

- [ ] StripeWebhookHandler enhanced with all event types
- [ ] StripeEvent entity created for idempotency
- [ ] Database migration applied
- [ ] WebhooksController updated
- [ ] Unit tests pass
- [ ] Integration tests with Stripe CLI pass
- [ ] Signature verification tested
- [ ] README updated with webhook testing instructions

## Estimated Effort

**3-4 days**

## Priority

⚠️ **HIGH** - Critical for payment reliability

## Dependencies

- Issue #10 (Hangfire Background Jobs) - webhook should queue async job
