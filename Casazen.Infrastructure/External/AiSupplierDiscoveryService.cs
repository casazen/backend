using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public partial class AiSupplierDiscoveryService(
    IWebSearchClient webSearch,
    IAiProvider aiProvider,
    ILogger<AiSupplierDiscoveryService> logger) : IAiSupplierDiscoveryService
{
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresAt, IReadOnlyList<ExternalSupplierSuggestion> Items)> Cache = new();

    private static readonly Dictionary<string, string> CategoryQueries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cleaning"] = "servizi pulizie affitti brevi",
        ["maintenance"] = "manutenzione casa",
        ["plumbing"] = "idraulico",
        ["laundry"] = "lavanderia",
    };

    public async Task<IReadOnlyList<ExternalSupplierSuggestion>> SearchNearbyAsync(
        string city,
        string category,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{city}:{category}".ToLowerInvariant();
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Items;

        var queryTerm = CategoryQueries.GetValueOrDefault(category, category);
        var searchQuery =
            $"Cerca fino a 5 attività reali di {queryTerm} a {city}, Italia. Includi nome, indirizzo, telefono, recensioni e sito web se disponibili.";

        var searchContent = await webSearch.SearchAsync(searchQuery, cancellationToken);
        if (string.IsNullOrWhiteSpace(searchContent))
            return [];

        try
        {
            var extractPrompt =
                """
                Estrai fornitori locali dal testo seguente e rispondi SOLO con JSON valido:
                {"suggestions":[{"name":"...","address":"...","phone":"...","email":null,"rating":4.5,"reviewCount":10,"websiteUrl":null,"mapsUrl":"https://..."}]}
                Massimo 5 elementi. Solo attività in Italia. Testo:
                """ + searchContent;

            var ai = await aiProvider.GenerateAsync(extractPrompt, AiModelTier.Economy, $"supplier-discovery:{cacheKey}", cancellationToken);
            var suggestions = ParseSuggestions(ai.Content);
            if (suggestions.Count > 0)
                Cache[cacheKey] = (DateTime.UtcNow.AddHours(24), suggestions);

            return suggestions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI supplier discovery failed for {City} / {Category}", city, category);
            return [];
        }
    }

    public static IReadOnlyList<ExternalSupplierSuggestion> ParseSuggestions(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var json = ExtractJson(content);
        if (json is null)
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("suggestions", out var suggestions) || suggestions.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<ExternalSupplierSuggestion>();
            foreach (var item in suggestions.EnumerateArray().Take(5))
            {
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                results.Add(new ExternalSupplierSuggestion(
                    name,
                    item.TryGetProperty("address", out var addrEl) ? addrEl.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("phone", out var phoneEl) ? phoneEl.GetString() : null,
                    item.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null,
                    item.TryGetProperty("rating", out var ratingEl) && ratingEl.ValueKind == JsonValueKind.Number ? ratingEl.GetDouble() : null,
                    item.TryGetProperty("reviewCount", out var reviewsEl) && reviewsEl.ValueKind == JsonValueKind.Number ? reviewsEl.GetInt32() : null,
                    item.TryGetProperty("mapsUrl", out var mapsEl) ? mapsEl.GetString() : null,
                    item.TryGetProperty("websiteUrl", out var webEl) ? webEl.GetString() : null,
                    "ai_web_search"));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ExtractJson(string content)
    {
        var fenced = JsonFenceRegex().Match(content);
        if (fenced.Success)
            return fenced.Groups[1].Value.Trim();

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();
}
