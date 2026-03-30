## User Story

As a **developer**, I want **resilient OTA API calls with retry, circuit breaker, and timeout policies**, so that **transient failures don't break the sync process and cascading failures are prevented**.

## Context

OTA APIs are unreliable and have rate limits. Without resilience patterns:
- Transient errors (503, timeout) fail immediately
- Cascading failures can overwhelm external services
- No automatic retry mechanism
- No rate limiting protection

## Technical Details

### Install Polly

```bash
dotnet add Casazen.Infrastructure package Microsoft.Extensions.Http.Polly
dotnet add Casazen.Infrastructure package Polly.Extensions.Http
```

### Resilience Patterns

1. **Retry Policy**: Exponential backoff (2s, 4s, 8s)
2. **Circuit Breaker**: Open after 5 failures in 30s, close after 60s
3. **Timeout**: 10s per request
4. **Rate Limiting**: Platform-specific (5/s for Airbnb, 10/s for Booking.com)

### Files to Create

1. **Casazen.Infrastructure/Resilience/PollyPolicies.cs**

```csharp
public static class PollyPolicies
{
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(ILogger logger)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError() // 5xx and 408
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    logger.LogWarning(
                        "Retry {RetryCount} after {Delay}s due to {StatusCode}",
                        retryCount, timespan.TotalSeconds, outcome.Result?.StatusCode);
                });
    }

    public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(ILogger logger)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(60),
                onBreak: (outcome, duration) =>
                {
                    logger.LogError(
                        "Circuit breaker OPEN for {Duration}s due to {StatusCode}",
                        duration.TotalSeconds, outcome.Result?.StatusCode);
                },
                onReset: () =>
                {
                    logger.LogInformation("Circuit breaker CLOSED");
                },
                onHalfOpen: () =>
                {
                    logger.LogInformation("Circuit breaker HALF-OPEN (testing)");
                });
    }

    public static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy()
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(10));
    }

    public static IAsyncPolicy<HttpResponseMessage> GetCombinedPolicy(ILogger logger)
    {
        var retryPolicy = GetRetryPolicy(logger);
        var circuitBreakerPolicy = GetCircuitBreakerPolicy(logger);
        var timeoutPolicy = GetTimeoutPolicy();

        // Order: Timeout → Retry → Circuit Breaker
        return Policy.WrapAsync(retryPolicy, circuitBreakerPolicy, timeoutPolicy);
    }
}
```

2. **Configure HttpClients in Program.cs**

```csharp
// Airbnb with Polly policies
builder.Services.AddHttpClient<AirbnbAdapter>(client =>
{
    client.BaseAddress = new Uri("https://api.airbnb.com/v2/");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddPolicyHandler((services, request) =>
{
    var logger = services.GetRequiredService<ILogger<AirbnbAdapter>>();
    return PollyPolicies.GetCombinedPolicy(logger);
});

// Booking.com with Polly policies
builder.Services.AddHttpClient<BookingAdapter>(client =>
{
    client.BaseAddress = new Uri("https://api.booking.com/v1/");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddPolicyHandler((services, request) =>
{
    var logger = services.GetRequiredService<ILogger<BookingAdapter>>();
    return PollyPolicies.GetCombinedPolicy(logger);
});

// Expedia with Polly policies
builder.Services.AddHttpClient<ExpediaAdapter>(client =>
{
    client.BaseAddress = new Uri("https://api.expedia.com/v1/");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddPolicyHandler((services, request) =>
{
    var logger = services.GetRequiredService<ILogger<ExpediaAdapter>>();
    return PollyPolicies.GetCombinedPolicy(logger);
});
```

3. **Per-Platform Rate Limiting**

```csharp
// Casazen.Infrastructure/Resilience/OtaRateLimiter.cs
public class OtaRateLimiter
{
    private readonly Dictionary<string, SemaphoreSlim> _rateLimiters = new();
    private readonly IConfiguration _config;

    public OtaRateLimiter(IConfiguration config)
    {
        _config = config;

        // Initialize rate limiters per platform
        _rateLimiters["Airbnb"] = new SemaphoreSlim(
            config.GetValue("OTA:Airbnb:RateLimitPerSecond", 5));

        _rateLimiters["BookingCom"] = new SemaphoreSlim(
            config.GetValue("OTA:BookingCom:RateLimitPerSecond", 10));

        _rateLimiters["Expedia"] = new SemaphoreSlim(
            config.GetValue("OTA:Expedia:RateLimitPerSecond", 3));
    }

    public async Task<T> ExecuteAsync<T>(string platform, Func<Task<T>> action)
    {
        var limiter = _rateLimiters[platform];
        await limiter.WaitAsync();

        try
        {
            return await action();
        }
        finally
        {
            // Release after 1 second (rate limit window)
            _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => limiter.Release());
        }
    }
}
```

4. **Usage in OTA Adapters**

```csharp
public class AirbnbAdapter : IChannelAdapter
{
    private readonly HttpClient _httpClient;
    private readonly OtaRateLimiter _rateLimiter;
    private readonly ILogger<AirbnbAdapter> _logger;

    public AirbnbAdapter(
        HttpClient httpClient,
        OtaRateLimiter rateLimiter,
        ILogger<AirbnbAdapter> logger)
    {
        _httpClient = httpClient;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<bool> SyncPropertyAsync(Guid propertyId, string externalId, string apiKey)
    {
        return await _rateLimiter.ExecuteAsync("Airbnb", async () =>
        {
            _logger.LogInformation("Syncing property {PropertyId} to Airbnb", propertyId);

            // HttpClient already has Polly policies attached
            var response = await _httpClient.PutAsJsonAsync(
                $"listings/{externalId}",
                new { /* ... */ }
            );

            return response.IsSuccessStatusCode;
        });
    }
}
```

5. **Configuration (appsettings.json)**

```json
{
  "OTA": {
    "Airbnb": {
      "RateLimitPerSecond": 5,
      "RetryAttempts": 3,
      "CircuitBreakerThreshold": 5,
      "TimeoutSeconds": 10
    },
    "BookingCom": {
      "RateLimitPerSecond": 10,
      "RetryAttempts": 3,
      "CircuitBreakerThreshold": 5,
      "TimeoutSeconds": 10
    },
    "Expedia": {
      "RateLimitPerSecond": 3,
      "RetryAttempts": 3,
      "CircuitBreakerThreshold": 5,
      "TimeoutSeconds": 15
    }
  }
}
```

6. **Register Rate Limiter**

```csharp
// Program.cs
builder.Services.AddSingleton<OtaRateLimiter>();
```

## Acceptance Criteria

- [ ] Polly NuGet packages installed
- [ ] Retry policy: 3 attempts with exponential backoff (2s, 4s, 8s)
- [ ] Circuit breaker: opens after 5 failures, closes after 60s
- [ ] Timeout policy: 10s per request
- [ ] Rate limiting: per-platform limits enforced
- [ ] All OTA adapters use resilience policies
- [ ] Logging for all policy events (retry, circuit breaker state)
- [ ] Unit tests simulate failures and verify retry behavior
- [ ] Integration tests verify circuit breaker opens/closes

## Testing

### Unit Tests

```csharp
[Fact]
public async Task AirbnbAdapter_TransientError_RetriesThreeTimes()
{
    // Arrange
    var mockHandler = new MockHttpMessageHandler();
    mockHandler.When("*").Respond(HttpStatusCode.ServiceUnavailable); // 503

    var httpClient = mockHandler.ToHttpClient();
    var adapter = new AirbnbAdapter(httpClient, new NullLogger<AirbnbAdapter>());

    // Act & Assert
    await Assert.ThrowsAsync<HttpRequestException>(() =>
        adapter.SyncPropertyAsync(Guid.NewGuid(), "test", "key"));

    // Verify 1 initial + 3 retries = 4 total requests
    Assert.Equal(4, mockHandler.GetMatchCount(new HttpRequestMessage()));
}

[Fact]
public async Task CircuitBreaker_FiveFailures_OpensCircuit()
{
    // Simulate 5 consecutive failures
    // Verify 6th call fails immediately (circuit open)
}
```

## Definition of Done

- [ ] Polly policies implemented
- [ ] HttpClients configured with policies
- [ ] Rate limiter implemented
- [ ] All OTA adapters use rate limiter
- [ ] Configuration in appsettings.json
- [ ] Unit tests pass
- [ ] Integration tests verify resilience
- [ ] README updated with resilience documentation

## Estimated Effort

**2 days**

## Priority

⚠️ **HIGH** - Required for production OTA reliability

## Dependencies

- Issue #9 (Airbnb OTA Adapter) - apply policies after adapter implementation

## Notes

- Polly policies are applied per HttpClient instance
- Circuit breaker state is shared across all requests to same endpoint
- Rate limiter uses SemaphoreSlim (in-memory, resets on app restart)
- For distributed rate limiting, use Redis with RedLock
