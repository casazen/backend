using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Typed HTTP client for the ConcordatoFacile status API with Polly resilience (ADR-008-R1 through ADR-008-R5).
/// Registered with retry policy (exponential backoff: 1s, 2s, 4s) and timeout policy (30s per attempt).
/// </summary>
public class ConcordatoFacileApiClient(HttpClient httpClient, ILogger<ConcordatoFacileApiClient> logger)
{
    /// <summary>
    /// Get all practices for a lease. Returns empty list when lease has no practices (ADR-008-R6).
    /// </summary>
    public async Task<List<PracticeDto>> GetPracticesByLeaseIdAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var requestUri = $"/v1/leases/{leaseId}/practices";

        try
        {
            using var response = await SendWithRateLimitHandlingAsync(
                () => httpClient.GetAsync(requestUri, cancellationToken),
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var practices = await response.Content.ReadFromJsonAsync<List<PracticeDto>>(cancellationToken);
            return practices ?? [];
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to fetch practices for lease {LeaseId}", leaseId);
            throw;
        }
    }

    /// <summary>
    /// Get a practice by ID. Returns null when practice is not found (404) (ADR-008).
    /// </summary>
    public async Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(practiceId);

        var requestUri = $"/v1/practices/{practiceId}";

        try
        {
            using var response = await SendWithRateLimitHandlingAsync(
                () => httpClient.GetAsync(requestUri, cancellationToken),
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<PracticeDto>(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to fetch practice {PracticeId}", practiceId);
            throw;
        }
    }

    /// <summary>
    /// Handle 429 rate limiting with Retry-After header (ADR-008-R5). If Retry-After is present and <= 30s,
    /// waits and retries once (not counted against the retry policy limit). If missing or > 30s, throws
    /// <see cref="CfRateLimitException"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRateLimitHandlingAsync(
        Func<Task<HttpResponseMessage>> sendRequest,
        CancellationToken cancellationToken)
    {
        var response = await sendRequest();

        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return response;
        }

        // Read Retry-After header
        var retryAfter = GetRetryAfterDelay(response);

        if (retryAfter is null || retryAfter.Value.TotalSeconds > 30)
        {
            var retryAfterValue = response.Headers.RetryAfter?.ToString() ?? "missing";
            logger.LogWarning("CF API rate limit exceeded; Retry-After: {RetryAfter}", retryAfterValue);
            response.Dispose();
            throw new CfRateLimitException(
                $"ConcordatoFacile API rate limit exceeded. Retry-After: {retryAfterValue}");
        }

        // Wait and retry once
        logger.LogInformation("CF API rate limit exceeded; waiting {Seconds}s before retry", retryAfter.Value.TotalSeconds);
        await Task.Delay(retryAfter.Value, cancellationToken);
        response.Dispose();

        return await sendRequest();
    }

    /// <summary>
    /// Parse Retry-After header as seconds (integer) or HTTP-date (RFC 7231).
    /// </summary>
    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is null)
        {
            return null;
        }

        // Retry-After: <seconds>
        if (response.Headers.RetryAfter.Delta.HasValue)
        {
            return response.Headers.RetryAfter.Delta.Value;
        }

        // Retry-After: <HTTP-date>
        if (response.Headers.RetryAfter.Date.HasValue)
        {
            var delay = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}

/// <summary>
/// Thrown when the ConcordatoFacile API returns 429 Too Many Requests and the Retry-After header is missing or > 30s
/// (ADR-008-R5).
/// </summary>
public sealed class CfRateLimitException : Exception
{
    public CfRateLimitException(string message) : base(message)
    {
    }
}
