using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>Public Open Data Hub event occurrences near properties in South Tyrol. The source record carries the original attribution.</summary>
public sealed class MarketingOpenEvents(HttpClient client, ILogger<MarketingOpenEvents> logger)
{
    public async Task<List<LocalEvent>> ReadAsync(Property property, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!string.Equals(property.Country, "IT", StringComparison.OrdinalIgnoreCase) ||
            property.Latitude is < 46.2m or > 47.1m || property.Longitude is < 10.3m or > 12.5m) return [];
        var path = FormattableString.Invariant($"v1/Event?language=it&active=true&pagesize=30&radius=10000&latitude={property.Latitude}&longitude={property.Longitude}&begindate={from:yyyy-MM-dd}&enddate={to:yyyy-MM-dd}&fields=Self,Detail,EventDate,LicenseInfo");
        try
        {
            using var response = await client.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode) return [];
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Read(doc.RootElement, from, to);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Open Data Hub is unavailable for the marketing event search");
            return [];
        }
    }

    internal static List<LocalEvent> Read(JsonElement root, DateOnly from, DateOnly to)
    {
        var events = new List<LocalEvent>();
        if (!root.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return events;
        foreach (var item in items.EnumerateArray())
        {
            var url = Text(item, "Self");
            if (!MarketingStudio.IsPublicUrl(url) || !item.TryGetProperty("Detail", out var detail) ||
                detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("it", out var italian) ||
                Text(italian, "Title") is not { Length: > 0 } title || !item.TryGetProperty("EventDate", out var dates) ||
                dates.ValueKind != JsonValueKind.Array || !item.TryGetProperty("LicenseInfo", out var license)) continue;
            var licenceName = Text(license, "License");
            if (licenceName is not ("CC0" or "CC-BY")) continue;
            var credit = Text(license, "Author") ?? Text(license, "LicenseHolder") ?? "Open Data Hub";
            foreach (var date in dates.EnumerateArray())
            {
                if (date.ValueKind != JsonValueKind.Object || date.TryGetProperty("IsCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True ||
                    date.TryGetProperty("Active", out var active) && active.ValueKind == JsonValueKind.False ||
                    Text(date, "From") is not { Length: >= 10 } raw || !DateOnly.TryParseExact(raw[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day) || day < from || day > to) continue;
                events.Add(new(title, day, url!, $"Open Data Hub · {credit} · {licenceName}", "confirmed"));
            }
        }
        return events.DistinctBy(e => (e.Name, e.Date)).OrderBy(e => e.Date).Take(12).ToList();
    }

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
}
