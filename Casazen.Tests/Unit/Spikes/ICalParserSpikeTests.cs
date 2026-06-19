using Casazen.Infrastructure.ICalSpike;
using Xunit;

namespace Casazen.Tests.Unit.Spikes;

/// <summary>F0 spike tests for #289 — iCal import/export PoC.</summary>
public class ICalParserSpikeTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-airbnb.ics"));

    [Fact]
    public void ParseImport_AirbnbFixture_ReturnsTwoBlocks()
    {
        var blocks = ICalImportSpike.ParseImport(LoadFixture());

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.False(string.IsNullOrWhiteSpace(b.ExternalUid)));
        Assert.Contains(blocks, b => b.Summary == "Reserved");
    }

    [Fact]
    public void Overlaps_WhenRangeInsideBlock_ReturnsTrue()
    {
        var blocks = ICalImportSpike.ParseImport(LoadFixture());
        var first = blocks[0];

        var overlaps = ICalImportSpike.Overlaps(
            first.StartUtc.AddHours(1),
            first.EndUtc.AddHours(-1),
            blocks);

        Assert.True(overlaps);
    }

    [Fact]
    public void Overlaps_WhenRangeClear_ReturnsFalse()
    {
        var blocks = ICalImportSpike.ParseImport(LoadFixture());

        var overlaps = ICalImportSpike.Overlaps(
            new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc),
            blocks);

        Assert.False(overlaps);
    }

    [Fact]
    public void BuildExportFeed_ProducesIcsCalendarDocument()
    {
        var blocks = ICalImportSpike.ParseImport(LoadFixture());
        Assert.NotEmpty(blocks);

        var export = ICalImportSpike.BuildExportFeed(blocks, "CasaZen Export");

        Assert.Contains("BEGIN:VCALENDAR", export, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BEGIN:VEVENT", export, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("END:VCALENDAR", export, StringComparison.OrdinalIgnoreCase);
    }
}
