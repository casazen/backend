using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E08/F01/US02/T01 (D7, ADR-023-R8): the quota of calls to Octorate is the org's, and a visitor of the booking site must not be able to spend
/// it. The public calendar and the public availability read CasaZen's own data (<c>PropertyOccupancy</c>, <c>CheckoutHolds</c>) and never ask
/// the channel manager: only the final check of a booking that is about to be written (<c>BookingService</c>) and the availability the host reads
/// before entering one (<c>HostAvailabilityService</c>) do, and both answer from the memory of <c>ChannelManagerAvailabilityService</c> for
/// 60 seconds. A source guard: it fails if one of the public readers names Octorate or the channel manager check.
/// </summary>
public class ChannelManagerQuotaArchitectureTests
{
    private static readonly string[] Forbidden = ["Octorate", "ChannelManagerAvailability"];

    [Theory]
    [InlineData("Casazen.Infrastructure/Services/PublicAvailabilityService.cs")]
    [InlineData("Casazen.Infrastructure/Services/PublicCalendarService.cs")]
    [InlineData("Casazen.Infrastructure/Services/PublicOccupancyReader.cs")]
    public void PublicReads_NeverAskTheChannelManager(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"{relativePath} moved: update the guard of the quota of Octorate.");

        var source = File.ReadAllText(path);

        foreach (var word in Forbidden)
            Assert.DoesNotContain(word, source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
