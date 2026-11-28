namespace Casazen.Core.Suppliers;

/// <summary>
/// The typical time a supplier takes to answer, as the public showcase may show it (SP-09). <b>Measured, never written</b>: it
/// comes from the requests the supplier really took (<c>TakenAt − CreatedAt</c>, the same pairs as the average of
/// <c>GET api/supplier/today</c>, SP-04), over the last <see cref="Services.SupplierEarningsSummary.ResponseWindowDays"/> days,
/// and only once there are enough of them to mean something. A request accepted automatically (SP-04d) is born taken and nobody
/// answered it: it is not one of the pairs.
/// </summary>
public static class SupplierResponseTimes
{
    public static int? AverageMinutes(IReadOnlyCollection<double> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        return minutes.Count == 0 ? null : (int)Math.Round(minutes.Average());
    }

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
