using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Core.TouristTax;

/// <summary>Where the collector of a stay's tourist tax comes from (<see cref="TouristTaxCollectorRules.Decide"/>).</summary>
public enum TouristTaxCollectorSource
{
    /// <summary>The stay is not a channel stay (the host collects), or it is one whose collector nobody declared yet.</summary>
    Default = 0,

    /// <summary>An active channel rule of the comune decided it (ADR-037-R3).</summary>
    ChannelRule = 1,

    /// <summary>The host declared it for a channel stay with no rule for its comune.</summary>
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

/// <summary>
/// The rule of ADR-037-R3 (NF-46, AC-NF46-4, AC-NF46-8), pure: who collects the tourist tax of a stay. A guest has one line per booking, so the
/// same guest cannot be taxed twice whoever collects.
/// </summary>
public static class TouristTaxCollectorRules
{
    /// <summary>
    /// A channel booking whose comune has an active rule: the collector of the rule (<see cref="TouristTaxCollector.Channel"/> means the channel
    /// collects and pays the comune). A <see cref="BookingSource.Direct"/>, <see cref="BookingSource.Manual"/> or <see cref="BookingSource.Local"/>
    /// booking: the host. A channel booking with no rule: the host's declaration, and without it no collector yet
    /// (<see cref="TouristTaxCollectorDecision.CollectorNeeded"/>).
    /// </summary>
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

    /// <summary>True for a booking of a channel (an OTA): the only ones whose collector can be the channel.</summary>
    public static bool IsChannelBooking(BookingSource source) => FiscalCopy.IsOtaBookingSource(source);
}
