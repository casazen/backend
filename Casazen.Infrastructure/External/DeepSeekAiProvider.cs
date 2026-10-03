using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// DeepSeek chat completions. No cache of its own (A8-25): a caller that may reuse an answer asks
/// <see cref="IAiResponseCache"/> first, which is bounded and expires; <c>cacheKey</c> is only the label of the request.
/// </summary>
public class DeepSeekAiProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AiOptions> options,
    ILogger<DeepSeekAiProvider> logger) : IAiProvider
{
    public async Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogDebug("Ai:ApiKey not configured; returning empty AI response.");
            return new AiGenerationResult(string.Empty, 0, 0, tier, FromCache: false, ProviderConfigured: false);
        }

        var baseUrl = config.OpenAiBaseUrl.TrimEnd('/');
        var payload = new
        {
            model = config.Model,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = config.MaxCompletionTokens,
            temperature = 0.2,
        };

        var client = httpClientFactory.CreateClient("DeepSeek");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var content = ExtractCompletionContent(json);
        var (promptTokens, completionTokens) = ExtractUsage(json);
        return new AiGenerationResult(content, promptTokens, completionTokens, tier, false);
    }

    public static string ExtractCompletionContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return string.Empty;

        var message = choices[0].GetProperty("message");
        return message.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// <c>usage.prompt_tokens</c> / <c>usage.completion_tokens</c> of a chat completion (A8-07), <c>(0, 0)</c> when the
    /// response has no usage block: the budget guard then estimates them.
    /// </summary>
    public static (int PromptTokens, int CompletionTokens) ExtractUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0);

        return (ReadCount(usage, "prompt_tokens"), ReadCount(usage, "completion_tokens"));
    }

    internal static int ReadCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? Math.Max(0, count)
            : 0;
}
