namespace Casazen.Core.Suppliers;

/// <summary>
/// The typical time a supplier takes to answer, as the public showcase may show it (SP-09). <b>Measured, never written</b>: it
/// comes from the requests the supplier really took (<c>TakenAt − CreatedAt</c>, the same pairs as the average of
/// <c>GET api/supplier/today</c>, SP-04), over the last <see cref="Services.SupplierEarningsSummary.ResponseWindowDays"/> days,
/// and only once there are enough of them to mean something.
/// </summary>
public static class SupplierResponseTimes
{
    /// <summary>
    /// The median of <paramref name="minutes"/> (the minutes each request waited for the supplier's answer), in whole minutes
    /// (a half minute rounds up); <c>null</c> when there are fewer than <see cref="PublicShowcaseLimits.ResponseTimeMinSamples"/>:
    /// below that the public page shows no response time at all. A median, not an average: one request left for a weekend
    /// does not turn a supplier that answers in minutes into one that answers in hours. A negative wait (a clock that moved) is
    /// read as zero. With an even number of requests the median is the middle of the two central ones.
    /// </summary>
    public static int? PublicMedianMinutes(IReadOnlyCollection<double> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        if (minutes.Count < PublicShowcaseLimits.ResponseTimeMinSamples)
            return null;

        var sorted = minutes.Select(m => Math.Max(0, m)).Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return (int)Math.Round(median, MidpointRounding.AwayFromZero);
    }
}
