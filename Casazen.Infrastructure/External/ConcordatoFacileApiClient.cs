using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Typed HTTP client for the ConcordatoFacile status API with Polly resilience (ADR-008-R1 through ADR-008-R5). The shapes it reads are
/// those of the specification v0.2 (<c>https://stato.concordatofacile.it</c>, key in <c>Authorization: Bearer</c>), which prevails on the
/// array that ADR-008-R6 describes.
/// Registered with retry policy (exponential backoff: 1s, 2s, 4s) and timeout policy (30s per attempt).
/// </summary>
public class ConcordatoFacileApiClient(HttpClient httpClient, ILogger<ConcordatoFacileApiClient> logger)
{
    /// <summary>
    /// The practices of a lease, newest first (v0.2 §4, <c>GET /v1/leases/{leaseId}/practices</c>). The body is an object
    /// <c>{ "leaseId": ..., "practices": [...] }</c>, also when the list is empty: a lease with no practices, unknown to ConcordatoFacile or
    /// not CasaZen's is a 200 with <c>"practices": []</c>, never a 404; a body without the list is read as no practices. Any other
    /// answer throws (401 key rejected, 429 after the wait, 5xx after the retries): the caller keeps its last valid snapshot as stale
    /// and must not take the error for an empty list (ADR-008-R8).
    /// </summary>
    public virtual async Task<List<PracticeDto>> GetPracticesByLeaseIdAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var requestUri = $"/v1/leases/{leaseId}/practices";

        try
        {
            using var response = await SendWithRateLimitHandlingAsync(
                () => httpClient.GetAsync(requestUri, cancellationToken),
                cancellationToken);

            await EnsureSuccessAsync(response, $"the practices of lease {leaseId}", cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<LeasePracticesDto>(cancellationToken);
            return body?.Practices ?? [];
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to fetch practices for lease {LeaseId}", leaseId);
            throw;
        }
    }

    /// <summary>
    /// A practice by its number (v0.2 §4, <c>GET /v1/practices/{practiceId}</c>), with its <c>leaseId</c>, which is null when the practice
    /// is not linked to a lease. Returns null when ConcordatoFacile does not know it: the 404 <c>practice_not_found</c> is the same for a
    /// practice that does not exist, is of another partner or of the other environment, and a 400 <c>invalid_practice_id</c> is a number it
    /// does not accept (ADR-008).
    /// </summary>
    public virtual async Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(practiceId);

        var requestUri = $"/v1/practices/{Uri.EscapeDataString(practiceId)}";

        try
        {
            using var response = await SendWithRateLimitHandlingAsync(
                () => httpClient.GetAsync(requestUri, cancellationToken),
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.BadRequest
                && await TryReadProblemCodeAsync(response, cancellationToken) == "invalid_practice_id")
            {
                return null;
            }

            await EnsureSuccessAsync(response, $"practice {practiceId}", cancellationToken);

            return await response.Content.ReadFromJsonAsync<PracticeDto>(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to fetch practice {PracticeId}", practiceId);
            throw;
        }
    }

    /// <summary>
    /// Throws for any answer that is not a success, naming the <c>code</c> of its <c>application/problem+json</c> body (v0.2 §4:
    /// unauthorized, rate_limited, invalid_lease_id...). A 401 means the key is missing, wrong, revoked or of the other environment, which
    /// only the operators can fix: it is logged as an error.
    /// </summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = await TryReadProblemCodeAsync(response, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogError(
                "ConcordatoFacile status API rejected the key ({Code}) reading {What}: it is missing, wrong, revoked or of the other environment",
                code,
                what);
        }

        throw new HttpRequestException(
            $"ConcordatoFacile status API answered {(int)response.StatusCode} {code} reading {what}.",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static async Task<string?> TryReadProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // As a string, so that the body can be read again by whoever comes next.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
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
