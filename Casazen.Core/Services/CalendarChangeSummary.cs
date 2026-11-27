using System.Globalization;
using System.Text.Json;

namespace Casazen.Core.Services;

/// <summary>
/// The <c>SummaryJson</c> of a calendar change process (E08/F05/US02/T01, NF-36): the property ids and the dates of its windows, written by the recorder
/// whatever the caller says, and what the caller adds under <c>detail</c> (fields, actions and values: never a name, an address or a guest). The property ids
/// are what the read of the changes narrows by the reach of the caller, so a change of a property nobody can reach is never shown to someone who cannot.
/// </summary>
public static class CalendarChangeSummary
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// The summary of the windows <paramref name="windows"/> with the <paramref name="detailJson"/> the caller gave (a JSON object, or <c>null</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="detailJson"/> is not a JSON object.</exception>
    public static string Build(IReadOnlyCollection<CalendarWindow> windows, string? detailJson)
    {
        ArgumentNullException.ThrowIfNull(windows);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("propertyIds");
            foreach (var id in windows.Select(w => w.PropertyId).Distinct())
                writer.WriteStringValue(id);

            writer.WriteEndArray();
            if (windows.Count > 0)
            {
                writer.WriteString("from", Format(windows.Min(w => w.From)));
                writer.WriteString("to", Format(windows.Max(w => w.To)));
            }

            if (!string.IsNullOrWhiteSpace(detailJson))
            {
                try
                {
                    using var detail = JsonDocument.Parse(detailJson);
                    if (detail.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("The detail of a calendar change is a JSON object.", nameof(detailJson));

                    writer.WritePropertyName("detail");
                    detail.RootElement.WriteTo(writer);
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException("The detail of a calendar change is not valid JSON.", nameof(detailJson), ex);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The ids of the properties a summary names: empty for a summary that cannot be read (a process of before this rule, a hand-written row), which no
    /// restricted reach then sees.
    /// </summary>
    public static IReadOnlyList<Guid> PropertyIdsOf(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
            return [];

        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("propertyIds", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return ids.EnumerateArray()
                .Select(id => id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) ? parsed : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The first and the last day a summary names; <c>null</c> for a summary that has none.</summary>
    public static (DateOnly From, DateOnly To)? DatesOf(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("from", out var from)
                && root.TryGetProperty("to", out var to)
                && from.ValueKind == JsonValueKind.String
                && to.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(from.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
                && DateOnly.TryParseExact(to.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
            {
                return (first, last);
            }
        }
        catch (JsonException)
        {
            // A summary that cannot be read names no dates.
        }

        return null;
    }

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
