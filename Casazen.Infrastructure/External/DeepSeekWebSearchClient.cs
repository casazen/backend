using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Web search through the DeepSeek Anthropic-compatible endpoint (AI supplier discovery, behind
/// <c>Features:AiSupplierDiscovery</c>). Every call goes through the platform AI budget: reserved before the request
/// (<see cref="AiBudgetExceededException"/> when it does not fit), settled with the reported <c>usage</c>.
/// </summary>
public class DeepSeekWebSearchClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> options,
    IAiBudgetGuard budget,
    ILogger<DeepSeekWebSearchClient> logger) : IWebSearchClient
{
    public async Task<string?> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogDebug("Ai:ApiKey not configured; skipping web search.");
            return null;
        }

        var baseUrl = config.AnthropicBaseUrl.TrimEnd('/');
        var payload = new
        {
            model = config.Model,
            max_tokens = config.WebSearchMaxTokens,
            messages = new[] { new { role = "user", content = query } },
            tools = new[] { new { type = "web_search_20250305", name = "web_search" } },
        };

        // Before the request: a search that does not fit the monthly budget is never sent.
        var reservation = await budget.ReserveAsync(
            AiTokenEstimator.EstimateCall(query, config.WebSearchMaxTokens),
            cancellationToken);
        long usedTokens = AiTokenEstimator.Estimate(query);

        try
        {
            var client = httpClientFactory.CreateClient("DeepSeek");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
            request.Headers.Add("x-api-key", config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("DeepSeek web search failed with status {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var text = ExtractTextContent(json);
            var (inputTokens, outputTokens) = ExtractUsage(json);
            var (prompt, completion) = AiTokenEstimator.UsedOrEstimated(inputTokens, outputTokens, query, text);
            usedTokens = prompt + completion;
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The query is built from the property's city and a fixed category: no personal data in this log.
            logger.LogWarning(ex, "DeepSeek web search failed for query {Query}", query);
            return null;
        }
        finally
        {
            await SettleSafelyAsync(reservation, usedTokens);
        }
    }

    /// <summary><c>usage.input_tokens</c> / <c>usage.output_tokens</c> of an Anthropic-format response, (0, 0) when absent.</summary>
    public static (int InputTokens, int OutputTokens) ExtractUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0);

        return (DeepSeekAiProvider.ReadCount(usage, "input_tokens"), DeepSeekAiProvider.ReadCount(usage, "output_tokens"));
    }

    private async Task SettleSafelyAsync(AiBudgetReservation reservation, long usedTokens)
    {
        try
        {
            await budget.SettleAsync(reservation, usedTokens, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not settle an AI budget reservation of {ReservedTokens} tokens", reservation.ReservedTokens);
        }
    }

    public static string? ExtractTextContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text"
                && block.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }
}
