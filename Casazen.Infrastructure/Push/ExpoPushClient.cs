using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Push;

/// <summary>
/// Configuration of the Expo push service (section <c>Expo</c>; on Railway <c>Expo__AccessToken</c>). The access token
/// is optional: Expo requires it only when "Enhanced Security for Push Notifications" is enabled for the project
/// (runbook <c>docs/runbooks/mobile-release.md</c> § 9.7). It is a secret: never in the repository.
/// </summary>
public sealed class ExpoPushOptions
{
    public const string SectionName = "Expo";

    /// <summary>Base address of the Expo push API (HTTPS).</summary>
    public static readonly Uri ApiBaseAddress = new("https://exp.host/");

    /// <summary>Expo accepts at most 100 messages per send request.</summary>
    public const int MaxMessagesPerRequest = 100;

    /// <summary>Expo accepts at most 1000 ticket ids per receipts request.</summary>
    public const int MaxReceiptIdsPerRequest = 1000;

    public string? AccessToken { get; set; }

    public bool HasAccessToken => !string.IsNullOrWhiteSpace(AccessToken);
}

/// <summary>One message of a send request (Expo push API v2).</summary>
public sealed class ExpoPushMessage
{
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>Android notification channel created by the app (MO-03); ignored on iOS.</summary>
    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public Dictionary<string, string> Data { get; set; } = new();
}

/// <summary>What happened to a send request as a whole.</summary>
public enum ExpoSendOutcome
{
    /// <summary>Expo answered with one ticket per message, in order (<see cref="ExpoSendResult.Tickets"/>).</summary>
    Accepted,

    /// <summary>Expo certainly did not take the messages (HTTP 429 or 5xx, connection not established): retry later.</summary>
    NotSent,

    /// <summary>Expo refused the request (other 4xx, e.g. 401 for a wrong access token): retrying does not help.</summary>
    Refused,

    /// <summary>No usable answer after the request was sent (timeout, connection lost): Expo may have taken the messages.</summary>
    Unknown,
}

/// <summary>Ticket of one message: <c>ok</c> with the id of its receipt, or <c>error</c> with Expo's error code.</summary>
public sealed record ExpoPushTicket(bool Ok, string? Id, string? Error);

/// <summary>Result of <see cref="IExpoPushClient.SendAsync"/>. <see cref="Error"/> is a code for the logs, never a token.</summary>
public sealed record ExpoSendResult(ExpoSendOutcome Outcome, IReadOnlyList<ExpoPushTicket> Tickets, string? Error = null)
{
    public static ExpoSendResult Failure(ExpoSendOutcome outcome, string error) => new(outcome, [], error);
}

/// <summary>Receipt of one ticket.</summary>
public sealed record ExpoPushReceipt(bool Ok, string? Error);

/// <summary>
/// Result of <see cref="IExpoPushClient.GetReceiptsAsync"/>: the receipts Expo has (a ticket without one is not ready yet,
/// or older than a day), or <see cref="Error"/> when the request failed.
/// </summary>
public sealed record ExpoReceiptsResult(IReadOnlyDictionary<string, ExpoPushReceipt> Receipts, string? Error = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>Expo push API: send (at most 100 messages) and receipts (at most 1000 ids). Mocked in the tests.</summary>
public interface IExpoPushClient
{
    Task<ExpoSendResult> SendAsync(IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken = default);

    Task<ExpoReceiptsResult> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP client of the Expo push API, with the access token of <see cref="ExpoPushOptions"/> when configured. It never
/// throws for Expo's answers: every failure is classified (<see cref="ExpoSendOutcome"/>) so the caller knows whether the
/// messages may have been taken. Expo's error <c>message</c> texts contain the push token and are never read.
/// </summary>
public sealed class ExpoPushClient(
    HttpClient httpClient,
    IOptions<ExpoPushOptions> options,
    ILogger<ExpoPushClient> logger) : IExpoPushClient
{
    private const string SendPath = "--/api/v2/push/send";
    private const string ReceiptsPath = "--/api/v2/push/getReceipts";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ExpoSendResult> SendAsync(IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Count is 0 or > ExpoPushOptions.MaxMessagesPerRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(messages), messages.Count, $"A send request carries 1 to {ExpoPushOptions.MaxMessagesPerRequest} messages.");
        }

        HttpResponseMessage response;
        try
        {
            using var request = CreateRequest(SendPath, messages);
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (NotConnected(ex))
        {
            logger.LogWarning("Expo push send not delivered: connection failed ({Error})", ex.HttpRequestError);
            return ExpoSendResult.Failure(ExpoSendOutcome.NotSent, $"Connection:{ex.HttpRequestError}");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Expo push send without answer ({Error})", ex.HttpRequestError);
            return ExpoSendResult.Failure(ExpoSendOutcome.Unknown, $"Connection:{ex.HttpRequestError}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Expo push send timed out after {Seconds} s", httpClient.Timeout.TotalSeconds);
            return ExpoSendResult.Failure(ExpoSendOutcome.Unknown, "Timeout");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
            {
                logger.LogWarning("Expo push send answered HTTP {Status}: will be retried", status);
                return ExpoSendResult.Failure(ExpoSendOutcome.NotSent, $"Http{status}");
            }

            if (!response.IsSuccessStatusCode)
            {
                LogRefused("send", response.StatusCode);
                return ExpoSendResult.Failure(ExpoSendOutcome.Refused, $"Http{status}");
            }

            ExpoSendResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ExpoSendResponse>(JsonOpts, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or NotSupportedException)
            {
                logger.LogWarning("Expo push send answered HTTP {Status} with an unreadable body", status);
                return ExpoSendResult.Failure(ExpoSendOutcome.Unknown, "UnreadableResponse");
            }

            if (body?.Data is not { } tickets || tickets.Count != messages.Count)
            {
                logger.LogWarning(
                    "Expo push send answered {Tickets} tickets for {Messages} messages",
                    body?.Data?.Count ?? 0,
                    messages.Count);
                return ExpoSendResult.Failure(ExpoSendOutcome.Unknown, "TicketCountMismatch");
            }

            return new ExpoSendResult(
                ExpoSendOutcome.Accepted,
                tickets
                    .Select(t => string.Equals(t.Status, "ok", StringComparison.OrdinalIgnoreCase)
                        ? new ExpoPushTicket(true, t.Id, null)
                        : new ExpoPushTicket(false, t.Id, ErrorCode(t.Details)))
                    .ToList());
        }
    }

    public async Task<ExpoReceiptsResult> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken = default)
    {
        if (ticketIds.Count is 0 or > ExpoPushOptions.MaxReceiptIdsPerRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticketIds), ticketIds.Count, $"A receipts request carries 1 to {ExpoPushOptions.MaxReceiptIdsPerRequest} ids.");
        }

        try
        {
            using var request = CreateRequest(ReceiptsPath, new { ids = ticketIds });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 401 or 403)
                    LogRefused("receipts", response.StatusCode);
                else
                    logger.LogWarning("Expo push receipts answered HTTP {Status}", (int)response.StatusCode);
                return new ExpoReceiptsResult(new Dictionary<string, ExpoPushReceipt>(), $"Http{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<ExpoReceiptsResponse>(JsonOpts, cancellationToken);
            var receipts = (body?.Data ?? [])
                .ToDictionary(
                    pair => pair.Key,
                    pair => string.Equals(pair.Value.Status, "ok", StringComparison.OrdinalIgnoreCase)
                        ? new ExpoPushReceipt(true, null)
                        : new ExpoPushReceipt(false, ErrorCode(pair.Value.Details)),
                    StringComparer.Ordinal);
            return new ExpoReceiptsResult(receipts);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Expo push receipts request failed ({ErrorType})", ex.GetType().Name);
            return new ExpoReceiptsResult(new Dictionary<string, ExpoPushReceipt>(), ex.GetType().Name);
        }
    }

    private HttpRequestMessage CreateRequest<T>(string path, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (options.Value.HasAccessToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken!.Trim());
        return request;
    }

    private void LogRefused(string operation, HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            logger.LogError(
                "Expo push {Operation} refused with HTTP {Status}: check Expo__AccessToken (required when the project enables enhanced push security)",
                operation,
                (int)statusCode);
            return;
        }

        logger.LogError("Expo push {Operation} refused with HTTP {Status}", operation, (int)statusCode);
    }

    /// <summary>The request never reached Expo: no connection, name resolution or TLS handshake.</summary>
    private static bool NotConnected(HttpRequestException ex) => ex.HttpRequestError is
        HttpRequestError.ConnectionError or
        HttpRequestError.NameResolutionError or
        HttpRequestError.SecureConnectionError or
        HttpRequestError.ProxyTunnelError;

    /// <summary>Expo's error code (<c>details.error</c>), bounded; never the <c>message</c>, which quotes the token.</summary>
    private static string ErrorCode(ExpoErrorDetails? details)
    {
        var code = details?.Error;
        if (string.IsNullOrWhiteSpace(code))
            return "Unknown";
        code = code.Trim();
        return code.Length <= 64 ? code : code[..64];
    }

    private sealed class ExpoSendResponse
    {
        [JsonPropertyName("data")]
        public List<ExpoTicketDto>? Data { get; set; }
    }

    private sealed class ExpoTicketDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("details")]
        public ExpoErrorDetails? Details { get; set; }
    }

    private sealed class ExpoReceiptsResponse
    {
        [JsonPropertyName("data")]
        public Dictionary<string, ExpoReceiptDto>? Data { get; set; }
    }

    private sealed class ExpoReceiptDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("details")]
        public ExpoErrorDetails? Details { get; set; }
    }

    private sealed class ExpoErrorDetails
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
