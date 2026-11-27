using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public class StubAiProvider(ILogger<StubAiProvider> logger) : IAiProvider
{
    public Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        if (tier != AiModelTier.Economy)
        {
            logger.LogWarning("SEO generation requested non-Economy tier {Tier}; downgrading to Economy", tier);
            tier = AiModelTier.Economy;
        }

        var content = $"<article><p>{ExtractComuneName(prompt)}: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p></article>";
        return Task.FromResult(new AiGenerationResult(
            content, PromptTokens: 120, CompletionTokens: 280, tier, FromCache: false, ProviderConfigured: false));
    }

    private static string ExtractComuneName(string prompt)
    {
        const string marker = "Comune:";
        var idx = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return "Comune";

        var rest = prompt[(idx + marker.Length)..];
        var end = rest.IndexOf('\n');
        return (end > 0 ? rest[..end] : rest).Trim();
    }
}
