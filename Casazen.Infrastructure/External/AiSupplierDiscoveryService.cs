using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// "Nearby businesses" suggested by a web search plus an LLM extraction (US-014, in freeze). Behind
/// <see cref="FeatureFlags.AiSupplierDiscovery"/> (D11, off by default): while off nothing is sent to the provider.
/// </summary>
/// <remarks>
/// With the flag on (A8-01, A8-14, A4-26): only the known categories reach the prompt, results (empty ones too) are
/// cached for 24 h per city and category, links are kept only when they are <c>https</c> (Google Maps links only on a
/// Google Maps host), and the LLM's rating and review count are dropped because they have no verifiable source.
/// </remarks>
public partial class AiSupplierDiscoveryService(
    IWebSearchClient webSearch,
    IAiProvider aiProvider,
    IFeatureFlags featureFlags,
    ILogger<AiSupplierDiscoveryService> logger) : IAiSupplierDiscoveryService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private static readonly ConcurrentDictionary<string, (DateTime ExpiresAt, IReadOnlyList<ExternalSupplierSuggestion> Items)> Cache = new();

    private static readonly Dictionary<string, string> CategoryQueries = new(StringComparer.Ordinal)
    {
        [ServiceCategories.Cleaning] = "servizi pulizie affitti brevi",
        [ServiceCategories.Maintenance] = "manutenzione casa",
        [ServiceCategories.Plumbing] = "idraulico",
        [ServiceCategories.Laundry] = "lavanderia",
    };

    public async Task<IReadOnlyList<ExternalSupplierSuggestion>> SearchNearbyAsync(
        string city,
        string category,
        CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.AiSupplierDiscovery))
        {
            logger.LogDebug("AI supplier discovery is behind the disabled feature flag; no provider call");
            return [];
        }

        // Allowlist: a free-text category never reaches the prompt or multiplies the cache keys (A8-01).
        if (!CategoryQueries.TryGetValue(category, out var queryTerm) || string.IsNullOrWhiteSpace(city))
            return [];

        var cacheKey = $"{city.Trim()}:{category}".ToLowerInvariant();
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Items;

        var searchQuery =
            $"Cerca fino a 5 attività reali di {queryTerm} a {city.Trim()}, Italia. Includi nome, indirizzo, telefono e sito web se disponibili.";

        try
        {
            var searchContent = await webSearch.SearchAsync(searchQuery, cancellationToken);
            if (string.IsNullOrWhiteSpace(searchContent))
                return Remember(cacheKey, []);

            var extractPrompt =
                """
                Estrai fornitori locali dal testo seguente e rispondi SOLO con JSON valido:
                {"suggestions":[{"name":"...","address":"...","phone":"...","email":null,"websiteUrl":null,"mapsUrl":"https://..."}]}
                Massimo 5 elementi. Solo attività in Italia. Testo:
                """ + searchContent;

            var ai = await aiProvider.GenerateAsync(extractPrompt, AiModelTier.Economy, $"supplier-discovery:{cacheKey}", cancellationToken);
            return Remember(cacheKey, ParseSuggestions(ai.Content));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiBudgetExceededException)
        {
            // A4-26: a failing search or extraction is not a server error; nothing is cached, so the next call retries.
            logger.LogWarning(ex, "AI supplier discovery failed for category {Category}", category);
            return [];
        }
    }

    private static IReadOnlyList<ExternalSupplierSuggestion> Remember(string cacheKey, IReadOnlyList<ExternalSupplierSuggestion> items)
    {
        Cache[cacheKey] = (DateTime.UtcNow.Add(CacheDuration), items);
        return items;
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
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var name = ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // No rating or review count: an LLM extraction is not a verifiable source (A8-14).
                results.Add(new ExternalSupplierSuggestion(
                    name,
                    ReadString(item, "address") ?? string.Empty,
                    ReadString(item, "phone"),
                    ReadString(item, "email"),
                    Rating: null,
                    ReviewCount: null,
                    GoogleMapsUrl: SafeGoogleMapsUrl(ReadString(item, "mapsUrl")),
                    WebsiteUrl: SafeHttpsUrl(ReadString(item, "websiteUrl")),
                    Source: "ai_web_search"));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>An absolute <c>https</c> URL, otherwise null (no <c>javascript:</c>, <c>http:</c> or relative links).</summary>
    public static string? SafeHttpsUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : null;

    /// <summary>A <c>https</c> link on a Google Maps host (<c>maps.google.*</c>, <c>www.google.*/maps</c>, <c>maps.app.goo.gl</c>), otherwise null.</summary>
    public static string? SafeGoogleMapsUrl(string? url)
    {
        var safe = SafeHttpsUrl(url);
        if (safe is null)
            return null;

        var uri = new Uri(safe);
        var host = uri.Host.ToLowerInvariant();
        var isMaps = GoogleMapsHostRegex().IsMatch(host)
            || (GoogleHostRegex().IsMatch(host) && uri.AbsolutePath.StartsWith("/maps", StringComparison.OrdinalIgnoreCase))
            || host == "maps.app.goo.gl";
        return isMaps ? safe : null;
    }

    [GeneratedRegex(@"^maps\.google\.[a-z]{2,3}(\.[a-z]{2})?$")]
    private static partial Regex GoogleMapsHostRegex();

    [GeneratedRegex(@"^(www\.)?google\.[a-z]{2,3}(\.[a-z]{2})?$")]
    private static partial Regex GoogleHostRegex();

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
