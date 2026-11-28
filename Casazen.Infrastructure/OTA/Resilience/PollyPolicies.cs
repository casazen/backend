using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Casazen.Infrastructure.OTA.Resilience;

public static class PollyPolicies
{
    /// <summary>
    /// Creates a retry policy with exponential backoff for transient HTTP failures
    /// </summary>
    /// <param name="retryCount">Number of retry attempts</param>
    /// <param name="logger">Logger for tracking retry attempts</param>
    /// <returns>Async retry policy</returns>
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(int retryCount, ILogger logger)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(
                retryCount,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    var platform = context.GetValueOrDefault("Platform", "Unknown");
                    logger.LogWarning(
                        "Retry {RetryCount} for {Platform} after {Delay}s. Reason: {Reason}",
                        retryCount,
                        platform,
                        timespan.TotalSeconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "Unknown"
                    );
                });
    }

    public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(
        int failureThreshold,
        TimeSpan durationOfBreak,
        ILogger logger)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .CircuitBreakerAsync(
                failureThreshold,
                durationOfBreak,
                onBreak: (outcome, duration, context) =>
                {
                    var platform = context.GetValueOrDefault("Platform", "Unknown");
                    logger.LogError(
                        "Circuit breaker opened for {Platform} for {Duration}s. Reason: {Reason}",
                        platform,
                        duration.TotalSeconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "Unknown"
                    );
                },
                onReset: (context) =>
                {
                    var platform = context.GetValueOrDefault("Platform", "Unknown");
                    logger.LogInformation("Circuit breaker reset for {Platform}", platform);
                },
                onHalfOpen: () =>
                {
                    logger.LogInformation("Circuit breaker half-open, testing if service recovered");
                });
    }

    public static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy(TimeSpan timeout, ILogger logger)
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(
            timeout,
            TimeoutStrategy.Optimistic,
            onTimeoutAsync: (context, timespan, task) =>
            {
                var platform = context.GetValueOrDefault("Platform", "Unknown");
                logger.LogWarning(
                    "Request to {Platform} timed out after {Timeout}s",
                    platform,
                    timespan.TotalSeconds
                );
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Combines all resilience policies into a single policy wrap
    /// </summary>
    /// <param name="retryCount">Number of retry attempts</param>
    /// <param name="circuitBreakerFailures">Failures before opening circuit</param>
    /// <param name="circuitBreakerDuration">How long circuit stays open</param>
    /// <param name="timeout">Request timeout</param>
    /// <param name="logger">Logger instance</param>
    /// <returns>Combined policy wrap</returns>
    public static IAsyncPolicy<HttpResponseMessage> GetCombinedPolicy(
        int retryCount,
        int circuitBreakerFailures,
        TimeSpan circuitBreakerDuration,
        TimeSpan timeout,
        ILogger logger)
    {
        var retryPolicy = GetRetryPolicy(retryCount, logger);
        var circuitBreakerPolicy = GetCircuitBreakerPolicy(circuitBreakerFailures, circuitBreakerDuration, logger);
        var timeoutPolicy = GetTimeoutPolicy(timeout, logger);

        // Order matters: Timeout -> Retry -> CircuitBreaker
        // This ensures timeouts are caught by retry, and persistent failures open the circuit
        return Policy.WrapAsync(retryPolicy, circuitBreakerPolicy, timeoutPolicy);
    }
}
