using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <param name="ProviderConfigured">
/// <c>false</c> when no external AI provider answered (stub provider, or DeepSeek without API key): <c>Content</c> is a
/// placeholder or empty, never text to publish (SE-01, A8-06).
/// </param>
/// <param name="ImagesRead">
/// Images of an <see cref="AiRequest"/> the model received: <c>0</c> for a text call, for a provider that cannot read
/// images (DeepSeek) and when the provider refused them and the text was sent alone.
/// </param>
public record AiGenerationResult(
    string Content, int PromptTokens, int CompletionTokens, AiModelTier TierUsed, bool FromCache,
    bool ProviderConfigured = true, int ImagesRead = 0);

public sealed record AiImage(string MediaType, byte[] Data);

/// <summary>
/// A call with the instructions apart (<paramref name="SystemPrompt"/>) and, for a model that can read them, images. The
/// prompts are scrubbed of personal data like every prompt; the images go as they are, so only images meant to be
/// public (the photos of a listing) may be sent.
/// </summary>
/// <param name="Prompt">The request itself: the data and the task.</param>
/// <param name="Images">Images the model looks at, in order; empty for a text call.</param>
/// <param name="SystemPrompt">The rules of the answer; <c>null</c> = none.</param>
/// <param name="Temperature"><c>null</c> = the provider default of the platform (0.2 for the chat completions).</param>
/// <param name="MaxCompletionTokens"><c>null</c> = <c>Ai:MaxCompletionTokens</c>.</param>
/// <param name="DisableThinking">Request a direct answer when the provider supports switching reasoning off.</param>
public sealed record AiRequest(
    string Prompt,
    IReadOnlyList<AiImage> Images,
    string? SystemPrompt = null,
    double? Temperature = null,
    int? MaxCompletionTokens = null,
    bool DisableThinking = false)
{
    public string ToTextPrompt() =>
        string.IsNullOrWhiteSpace(SystemPrompt) ? Prompt : $"{SystemPrompt}\n\n{Prompt}";
}

public interface IAiProvider
{
    Task<AiGenerationResult> GenerateAsync(
        string prompt, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default);

    Task<AiGenerationResult> GenerateAsync(
        AiRequest request, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default) =>
        GenerateAsync(request.ToTextPrompt(), tier, cacheKey, cancellationToken);
}
