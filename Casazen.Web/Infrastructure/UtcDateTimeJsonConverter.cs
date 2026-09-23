using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Utilities;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Reads every JSON <see cref="DateTime"/> of API requests as UTC (FD-06).
/// <list type="bullet">
/// <item>No offset ("2026-10-01", "2026-10-01T10:00:00"): the value is taken as UTC,
/// so a date without time becomes midnight UTC of that day.</item>
/// <item>"Z" or an explicit offset ("2026-10-01T10:00:00+02:00"): converted to the UTC instant.</item>
/// </list>
/// System.Text.Json also uses this converter for <see cref="Nullable{DateTime}"/>.
/// Writing keeps the default ISO 8601 format.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();

        // With an offset the default parser returns a server-local value: re-read the exact instant.
        return value.Kind == DateTimeKind.Local
            ? reader.GetDateTimeOffset().UtcDateTime
            : UtcDateTime.Normalize(value);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
