using System.Net;
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
    /// <summary>
    /// HTTP client of the calls made with an <see cref="AiRequest"/>: a model that reads photos and writes a long text takes longer than the
    /// 30 seconds of the client of the text calls (<c>AiProviderRegistration</c>).
    /// </summary>
    public const string LongCallHttpClientName = "AiLongCall";

    public Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(prompt, tier, cacheKey, options.Value, cancellationToken);

    /// <summary>Same call, with the settings in effect (server configuration or the admin override).</summary>
    public async Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        AiOptions config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogDebug("Ai:ApiKey not configured; returning empty AI response.");
            return new AiGenerationResult(string.Empty, 0, 0, tier, FromCache: false, ProviderConfigured: false);
        }

        // FD-21 (PO 2026-10-08): scrub PII from every prompt before it leaves CasaZen.
        prompt = PiiScrubber.Scrub(prompt);

        if (config.UsesAnthropicMessages())
            return await GenerateAnthropicAsync(prompt, tier, config, cancellationToken);

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

    private async Task<AiGenerationResult> GenerateAnthropicAsync(
        string prompt,
        AiModelTier tier,
        AiOptions config,
        CancellationToken cancellationToken)
    {
        var baseUrl = config.AnthropicBaseUrl.TrimEnd('/');
        var payload = new
        {
            model = config.Model,
            max_tokens = config.MaxCompletionTokens,
            messages = new[] { new { role = "user", content = prompt } },
        };

        var client = httpClientFactory.CreateClient("DeepSeek");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var content = DeepSeekWebSearchClient.ExtractTextContent(json) ?? string.Empty;
        var (promptTokens, completionTokens) = DeepSeekWebSearchClient.ExtractUsage(json);
        return new AiGenerationResult(content, promptTokens, completionTokens, tier, false);
    }

    public Task<AiGenerationResult> GenerateAsync(
        AiRequest request,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(request, tier, cacheKey, options.Value, cancellationToken);

    /// <summary>
    /// A call with instructions and images (<see cref="AiRequest"/>), with the settings in effect. The images go to the providers that read
    /// them (<see cref="AiOptions.AcceptsImages"/>): as <c>image_url</c> parts of the chat completions, as <c>image</c> blocks of the
    /// Anthropic messages. When the provider refuses the request because of them (a compatible service whose model reads text only) the
    /// call is repeated once with the text alone, and the result says that no image was read.
    /// </summary>
    public async Task<AiGenerationResult> GenerateAsync(
        AiRequest request,
        AiModelTier tier,
        string cacheKey,
        AiOptions config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogDebug("Ai:ApiKey not configured; returning empty AI response.");
            return new AiGenerationResult(string.Empty, 0, 0, tier, FromCache: false, ProviderConfigured: false);
        }

        // FD-21 (PO 2026-10-08): scrub PII from every prompt before it leaves CasaZen.
        var scrubbed = request with
        {
            Prompt = PiiScrubber.Scrub(request.Prompt),
            SystemPrompt = request.SystemPrompt is null ? null : PiiScrubber.Scrub(request.SystemPrompt),
        };

        if (config.AcceptsImages() && scrubbed.Images.Count > 0)
        {
            try
            {
                return await SendAsync(scrubbed, scrubbed.Images, tier, config, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.UnprocessableEntity
                or HttpStatusCode.UnsupportedMediaType
                or HttpStatusCode.RequestEntityTooLarge)
            {
                logger.LogWarning(
                    "AI provider {Provider} refused a request with {Images} images ({Status}): sent again with the text alone",
                    config.Provider, scrubbed.Images.Count, (int?)ex.StatusCode);
            }
        }

        return await SendAsync(scrubbed, [], tier, config, cancellationToken);
    }

    private async Task<AiGenerationResult> SendAsync(
        AiRequest request,
        IReadOnlyList<AiImage> images,
        AiModelTier tier,
        AiOptions config,
        CancellationToken cancellationToken)
    {
        var maxTokens = request.MaxCompletionTokens ?? config.MaxCompletionTokens;
        var client = httpClientFactory.CreateClient(LongCallHttpClientName);

        if (config.UsesAnthropicMessages())
        {
            var blocks = new List<object>();
            foreach (var image in images)
            {
                blocks.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = image.MediaType, data = Convert.ToBase64String(image.Data) },
                });
            }

            blocks.Add(new { type = "text", text = request.Prompt });
            var anthropicPayload = new Dictionary<string, object?>
            {
                ["model"] = config.Model,
                ["max_tokens"] = maxTokens,
                ["messages"] = new[] { new { role = "user", content = blocks } },
            };
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                anthropicPayload["system"] = request.SystemPrompt;
            if (request.Temperature is { } anthropicTemperature)
                anthropicPayload["temperature"] = anthropicTemperature;

            using var anthropicRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.AnthropicBaseUrl.TrimEnd('/')}/v1/messages");
            anthropicRequest.Headers.Add("x-api-key", config.ApiKey);
            anthropicRequest.Headers.Add("anthropic-version", "2023-06-01");
            anthropicRequest.Content = new StringContent(JsonSerializer.Serialize(anthropicPayload), Encoding.UTF8, "application/json");

            using var anthropicResponse = await client.SendAsync(anthropicRequest, cancellationToken);
            anthropicResponse.EnsureSuccessStatusCode();

            var anthropicJson = await anthropicResponse.Content.ReadAsStringAsync(cancellationToken);
            var (anthropicPrompt, anthropicCompletion) = DeepSeekWebSearchClient.ExtractUsage(anthropicJson);
            return new AiGenerationResult(
                DeepSeekWebSearchClient.ExtractTextContent(anthropicJson) ?? string.Empty,
                anthropicPrompt,
                anthropicCompletion,
                tier,
                FromCache: false,
                ImagesRead: images.Count);
        }

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        if (images.Count == 0)
        {
            messages.Add(new { role = "user", content = request.Prompt });
        }
        else
        {
            var parts = new List<object>();
            foreach (var image in images)
            {
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" },
                });
            }

            parts.Add(new { type = "text", text = request.Prompt });
            messages.Add(new { role = "user", content = parts });
        }

        var payload = new
        {
            model = config.Model,
            messages,
            max_tokens = maxTokens,
            temperature = request.Temperature ?? 0.2,
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.OpenAiBaseUrl.TrimEnd('/')}/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var (promptTokens, completionTokens) = ExtractUsage(json);
        return new AiGenerationResult(
            ExtractCompletionContent(json), promptTokens, completionTokens, tier, FromCache: false, ImagesRead: images.Count);
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
