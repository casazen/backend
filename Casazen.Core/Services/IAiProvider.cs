using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <param name="ProviderConfigured">
/// <c>false</c> when no external AI provider answered (stub provider, or DeepSeek without API key): <c>Content</c> is a
/// placeholder or empty, never text to publish (SE-01, A8-06).
/// </param>
public record AiGenerationResult(
    string Content, int PromptTokens, int CompletionTokens, AiModelTier TierUsed, bool FromCache,
    bool ProviderConfigured = true);

public interface IAiProvider
{
    Task<AiGenerationResult> GenerateAsync(
        string prompt, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default);
}
