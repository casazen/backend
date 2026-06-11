using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record AiGenerationResult(
    string Content, int PromptTokens, int CompletionTokens, AiModelTier TierUsed, bool FromCache);

public interface IAiProvider
{
    Task<AiGenerationResult> GenerateAsync(
        string prompt, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default);
}
