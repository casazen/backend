using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Core.TouristTax;

public enum TouristTaxCollectorSource
{
    Default = 0,

    ChannelRule = 1,

    HostDeclared = 2,
}

/// <summary>Who collects the tax of a stay, and what the host must collect.</summary>
/// <param name="Collector">The collector; null for a channel stay with no rule and no declaration of the host.</param>
/// <param name="Source">Where <paramref name="Collector"/> comes from.</param>
/// <param name="CollectorNeeded">True for a channel stay with no rule and no declaration: the host is asked who collected.</param>
public sealed record TouristTaxCollectorDecision(TouristTaxCollector? Collector, TouristTaxCollectorSource Source, bool CollectorNeeded)
{
    /// <summary>
    /// What the host must collect of <paramref name="total"/>: nothing when the channel collects it (the amounts per guest are kept, recorded per
    /// channel and comune), the total otherwise, including while the collector is not known (never an assumed 0).
    /// </summary>
    public decimal AmountDue(decimal total) => Collector == TouristTaxCollector.Channel ? 0m : total;
}

public static class TouristTaxCollectorRules
{
    public static TouristTaxCollectorDecision Decide(BookingSource source, TouristTaxChannelRule? rule, TouristTaxCollector? declared)
    {
        if (!IsChannelBooking(source))
            return new TouristTaxCollectorDecision(TouristTaxCollector.Host, TouristTaxCollectorSource.Default, CollectorNeeded: false);

        if (rule is { IsActive: true } && rule.Channel == source)
            return new TouristTaxCollectorDecision(rule.Collector, TouristTaxCollectorSource.ChannelRule, CollectorNeeded: false);

        return declared is { } host
            ? new TouristTaxCollectorDecision(host, TouristTaxCollectorSource.HostDeclared, CollectorNeeded: false)
            : new TouristTaxCollectorDecision(null, TouristTaxCollectorSource.Default, CollectorNeeded: true);
    }

    public static bool IsChannelBooking(BookingSource source) => FiscalCopy.IsOtaBookingSource(source);
}
