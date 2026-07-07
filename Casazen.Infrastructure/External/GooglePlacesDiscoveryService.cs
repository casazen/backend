using System.Text.Json;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public class GooglePlacesDiscoveryService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GooglePlacesDiscoveryService> logger) : IGooglePlacesDiscoveryService
{
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
        var apiKey = configuration["Google:PlacesApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogDebug("Google Places API key not configured; skipping external supplier search.");
            return [];
        }

        var queryTerm = CategoryQueries.GetValueOrDefault(category, category);
        var query = $"{queryTerm} {city} Italia";
        var client = httpClientFactory.CreateClient("GooglePlaces");
        var url =
            $"https://maps.googleapis.com/maps/api/place/textsearch/json?query={Uri.EscapeDataString(query)}&language=it&key={apiKey}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Google Places search failed with status {Status}", response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return [];

            var suggestions = new List<ExternalSupplierSuggestion>();
            foreach (var place in results.EnumerateArray().Take(5))
            {
                var name = place.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var address = place.TryGetProperty("formatted_address", out var addrEl)
                    ? addrEl.GetString() ?? string.Empty
                    : string.Empty;
                double? rating = place.TryGetProperty("rating", out var ratingEl) && ratingEl.ValueKind == JsonValueKind.Number
                    ? ratingEl.GetDouble()
                    : null;
                int? reviewCount = place.TryGetProperty("user_ratings_total", out var reviewsEl) && reviewsEl.ValueKind == JsonValueKind.Number
                    ? reviewsEl.GetInt32()
                    : null;
                var placeId = place.TryGetProperty("place_id", out var idEl) ? idEl.GetString() : null;
                var mapsUrl = placeId is not null
                    ? $"https://www.google.com/maps/place/?q=place_id:{placeId}"
                    : null;

                suggestions.Add(new ExternalSupplierSuggestion(
                    name,
                    address,
                    null,
                    null,
                    rating,
                    reviewCount,
                    mapsUrl,
                    null,
                    "google_places"));
            }

            return suggestions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Google Places search failed for {City} / {Category}", city, category);
            return [];
        }
    }
}
