## Problem

**Current Issue**: Webhook handlers and OTA sync operations are synchronous, causing:
- Webhook timeout failures (OTAs expect < 3 second response)
- Long-running sync operations blocking API requests
- No job retry mechanism for failures
- No visibility into background task status

## User Story

As a **developer**, I want **background job infrastructure using Hangfire**, so that **webhook handlers respond quickly, long-running operations don't block API threads, and failed jobs are automatically retried**.

## Technical Details

### Step 1: Install Hangfire (1 day)

**Install NuGet packages:**
```bash
dotnet add Casazen.Infrastructure package Hangfire.AspNetCore
dotnet add Casazen.Infrastructure package Hangfire.SqlServer
```

**Configure in Program.cs (Casazen.Web):**
```csharp
// Add Hangfire services
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfireServer();

// After app.Build()
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthorizationFilter() }
});
```

**Create authorization filter:**
```csharp
// Casazen.Web/Infrastructure/HangfireAuthorizationFilter.cs
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.IsInRole("Admin");
    }
}
```

### Step 2: Create Background Jobs (2 days)

**Files to create:**

1. **Casazen.Infrastructure/Jobs/OtaSyncJob.cs**
```csharp
public class OtaSyncJob
{
    private readonly IOtaManager _otaManager;
    private readonly ILogger<OtaSyncJob> _logger;

    public OtaSyncJob(IOtaManager otaManager, ILogger<OtaSyncJob> logger)
    {
        _otaManager = otaManager;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid propertyId)
    {
        _logger.LogInformation("Starting OTA sync for property {PropertyId}", propertyId);

        try
        {
            await _otaManager.SyncAllAsync(propertyId);
            _logger.LogInformation("OTA sync completed for property {PropertyId}", propertyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OTA sync failed for property {PropertyId}", propertyId);
            throw; // Hangfire will retry
        }
    }
}
```

2. **Casazen.Infrastructure/Jobs/BookingPullJob.cs**
```csharp
public class BookingPullJob
{
    private readonly IOtaManager _otaManager;

    public async Task ExecuteAsync(Guid propertyId)
    {
        await _otaManager.PullBookingsAsync(propertyId);
    }
}
```

3. **Casazen.Infrastructure/Jobs/EmailQueueProcessor.cs**
```csharp
public class EmailQueueProcessor
{
    private readonly INotificationService _notificationService;

    public async Task SendBookingConfirmationAsync(Guid bookingId)
    {
        await _notificationService.SendBookingConfirmationAsync(bookingId);
    }

    public async Task SendPaymentReceiptAsync(Guid paymentId)
    {
        await _notificationService.SendPaymentReceiptAsync(paymentId);
    }
}
```

### Step 3: Update Webhook Handlers (2 days)

**Before (synchronous - WRONG):**
```csharp
[HttpPost("stripe")]
public async Task<IActionResult> StripeWebhook([FromBody] Event stripeEvent)
{
    await _stripeWebhookHandler.HandleEventAsync(stripeEvent); // May take > 3s
    return Ok();
}
```

**After (asynchronous - CORRECT):**
```csharp
[HttpPost("stripe")]
public IActionResult StripeWebhook([FromBody] Event stripeEvent)
{
    // 1. Verify signature (< 100ms)
    if (!_stripeWebhookHandler.VerifySignature(Request.Headers, stripeEvent))
        return Unauthorized();

    // 2. Queue background job (< 50ms)
    BackgroundJob.Enqueue<StripeWebhookJob>(
        job => job.ProcessEventAsync(stripeEvent)
    );

    // 3. Return quickly (< 200ms total)
    return Ok();
}
```

**Create StripeWebhookJob:**
```csharp
// Casazen.Infrastructure/Jobs/StripeWebhookJob.cs
public class StripeWebhookJob
{
    private readonly IPaymentService _paymentService;
    private readonly ILogger<StripeWebhookJob> _logger;

    public async Task ProcessEventAsync(Event stripeEvent)
    {
        _logger.LogInformation("Processing Stripe event {EventType}", stripeEvent.Type);

        switch (stripeEvent.Type)
        {
            case "payment_intent.succeeded":
                await HandlePaymentSucceeded(stripeEvent);
                break;
            case "payment_intent.failed":
                await HandlePaymentFailed(stripeEvent);
                break;
            case "charge.refunded":
                await HandleRefund(stripeEvent);
                break;
        }
    }

    private async Task HandlePaymentSucceeded(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        var payment = await _paymentService.GetByTransactionIdAsync(paymentIntent.Id);

        if (payment != null)
        {
            await _paymentService.MarkAsCompletedAsync(payment.Id);

            // Queue confirmation email
            BackgroundJob.Enqueue<EmailQueueProcessor>(
                job => job.SendPaymentReceiptAsync(payment.Id)
            );
        }
    }
}
```

### Step 4: Recurring Jobs (Hourly OTA Sync) (1 day)

**In Program.cs after UseHangfireDashboard:**
```csharp
RecurringJob.AddOrUpdate<OtaSyncJob>(
    "ota-sync-all",
    job => job.SyncAllPropertiesAsync(),
    Cron.Hourly);

RecurringJob.AddOrUpdate<BookingPullJob>(
    "booking-pull-all",
    job => job.PullAllBookingsAsync(),
    Cron.Every(15).Minutes());
```

## Acceptance Criteria

- [ ] Hangfire installed and configured with SQL Server storage
- [ ] Hangfire dashboard accessible at `/hangfire` (admin only)
- [ ] OtaSyncJob created and tested
- [ ] BookingPullJob created and tested
- [ ] EmailQueueProcessor created and tested
- [ ] StripeWebhookJob created and tested
- [ ] Webhook handlers respond < 1 second
- [ ] Background jobs execute successfully
- [ ] Failed jobs are retried automatically (3 attempts with exponential backoff)
- [ ] Recurring jobs scheduled (hourly OTA sync, 15-min booking pull)
- [ ] Integration test: webhook queues job and returns 200 OK immediately

## Definition of Done

- [ ] Hangfire NuGet packages installed
- [ ] Configuration in Program.cs complete
- [ ] All 4 background job classes created
- [ ] Webhook handlers updated to use BackgroundJob.Enqueue
- [ ] Recurring jobs configured
- [ ] Dashboard secured with admin authorization
- [ ] Unit tests for background jobs
- [ ] Integration test for webhook → job queuing
- [ ] README updated with Hangfire documentation

## Estimated Effort

**5 days**

## Priority

🔥 **CRITICAL** - Required for production webhooks

## Dependencies

- None (should be done early in Sprint 1)

## Notes

- Hangfire uses the same SQL Server database for job storage (no additional infrastructure)
- Dashboard provides visibility into job status, retries, failures
- Jobs are persistent: survive application restarts
- Automatic retry with exponential backoff: 1 minute, 5 minutes, 15 minutes
