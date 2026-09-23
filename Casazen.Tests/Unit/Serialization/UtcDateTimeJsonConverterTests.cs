using System.Text.Json;
using Casazen.Web.Infrastructure;
using Xunit;

namespace Casazen.Tests.Unit.Serialization;

public class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new UtcDateTimeJsonConverter() },
    };

    private sealed record Payload(DateTime CheckInDate, DateTime? CheckOutDate);

    [Theory]
    [InlineData("2026-10-01", "2026-10-01T00:00:00")]
    [InlineData("2026-10-01T10:15:00", "2026-10-01T10:15:00")]
    [InlineData("2026-10-01T10:15:00Z", "2026-10-01T10:15:00")]
    [InlineData("2026-10-01T00:00:00+02:00", "2026-09-30T22:00:00")]
    [InlineData("2026-10-01T10:15:00.5-05:00", "2026-10-01T15:15:00.5")]
    public void Read_AnyIsoValue_ReturnsUtc(string json, string expectedUtc)
    {
        var payload = JsonSerializer.Deserialize<Payload>(
            $$"""{"checkInDate":"{{json}}","checkOutDate":"{{json}}"}""", Options)!;

        var expected = DateTime.SpecifyKind(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        Assert.Equal(expected, payload.CheckInDate);
        Assert.Equal(DateTimeKind.Utc, payload.CheckInDate.Kind);
        Assert.Equal(expected, payload.CheckOutDate);
        Assert.Equal(DateTimeKind.Utc, payload.CheckOutDate!.Value.Kind);
    }

    [Fact]
    public void Read_NullableNull_ReturnsNull()
    {
        var payload = JsonSerializer.Deserialize<Payload>(
            """{"checkInDate":"2026-10-01","checkOutDate":null}""", Options)!;

        Assert.Null(payload.CheckOutDate);
    }

    [Fact]
    public void Read_InvalidDate_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Payload>(
            """{"checkInDate":"01/10/2026"}""", Options));
    }

    [Fact]
    public void Write_UtcValue_KeepsIsoFormatWithZ()
    {
        var json = JsonSerializer.Serialize(
            new Payload(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null), Options);

        Assert.Contains("\"checkInDate\":\"2026-10-01T00:00:00Z\"", json);
    }
}
