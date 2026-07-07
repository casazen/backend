using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

public class DeepSeekAiProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> options,
    ILogger<DeepSeekAiProvider> logger) : IAiProvider
{
    private static readonly ConcurrentDictionary<string, AiGenerationResult> Cache = new();

    public async Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached with { FromCache = true };

        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogDebug("Ai:ApiKey not configured; returning empty AI response.");
            return new AiGenerationResult(string.Empty, 0, 0, tier, false);
        }

        var baseUrl = config.OpenAiBaseUrl.TrimEnd('/');
        var payload = new
        {
            model = config.Model,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = 2048,
            temperature = 0.2,
        };

        var client = httpClientFactory.CreateClient("DeepSeek");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = ExtractCompletionContent(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = new AiGenerationResult(content, 0, 0, tier, false);
        Cache[cacheKey] = result;
        return result;
    }

    public static string ExtractCompletionContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return string.Empty;

        var message = choices[0].GetProperty("message");
        return message.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? string.Empty : string.Empty;
    }
}
