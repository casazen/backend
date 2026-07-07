using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

public class DeepSeekWebSearchClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> options,
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
            max_tokens = 4096,
            messages = new[] { new { role = "user", content = query } },
            tools = new[] { new { type = "web_search_20250305", name = "web_search" } },
        };

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

            return ExtractTextContent(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DeepSeek web search failed for query {Query}", query);
            return null;
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
