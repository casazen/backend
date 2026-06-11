using System.Collections.Concurrent;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Stub AI provider for SEO generation. Economy tier only; template cache for unchanged regulatory data.
/// </summary>
public class StubAiProvider(ILogger<StubAiProvider> logger) : IAiProvider
{
    private static readonly ConcurrentDictionary<string, AiGenerationResult> TemplateCache = new();

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

        if (TemplateCache.TryGetValue(cacheKey, out var cached))
        {
            logger.LogInformation("SEO template cache hit for {CacheKey}", cacheKey);
            return Task.FromResult(cached with { FromCache = true });
        }

        var content = $"<article><p>{ExtractComuneName(prompt)}: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p></article>";
        var result = new AiGenerationResult(content, PromptTokens: 120, CompletionTokens: 280, tier, FromCache: false);
        TemplateCache[cacheKey] = result;
        return Task.FromResult(result);
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
