using Casazen.Core.Entities;

namespace Casazen.Core.Services;

// The channel costs report of the host (NF-98, E07/F03/US02/T02): what each channel costs, by accrual like the revenue of the Home. The gross is the Home's
// revenue of the stays; the commission and the net are what the channel manager stored on the imported bookings, pro rata per night as the revenue is; the
// processing fees of the direct stays are the ones the card provider reported. A cost nobody reported is "not available", never zero.

public enum ChannelCommissionMode
{
    DeductedFromPayout,

    InvoicedSeparately,

    Unknown,
}

public enum ChannelCostKind
{
    ChannelCommission,

    PaymentFee,

    NoCost,
}

/// <summary>
/// What one channel costs in the period, by accrual: the confirmed stays with a night in it, pro rata per night (<see cref="StayKpiRules"/>). A figure nobody
/// reported is null, never zero: a stay whose commission is not reported is counted apart (<see cref="StaysWithoutCommission"/>) and is not taken as free in the
/// commission, the net or the rate.
/// </summary>
/// <param name="Source">The channel; null for the total row.</param>
/// <param name="Kind">What the cost is (<see cref="ChannelCostKind"/>); null for the total row, which adds up costs of different kinds.</param>
/// <param name="StayCount">The confirmed stays with a night in the period.</param>
/// <param name="Gross">The Home's revenue of those stays (lodging plus cleaning, tourist tax excluded), euros to the cent.</param>
/// <param name="Commission">
/// The cost of the stays that report one, pro rata, to the cent; null when none of them does. For a direct stay it is the card provider's fees.
/// </param>
/// <param name="Net">
/// What is left of the stays whose cost is known: the net the channel reported where it did (<see cref="Booking.OtaNetAmount"/>), otherwise the gross minus the cost,
/// pro rata, to the cent; null when no stay has either. The stays whose cost is not reported are not in it.
/// </param>
/// <param name="CostRate">The cost over the gross of the stays that report it, from 0 to 1 to four decimals; null when no stay reports a cost or that gross is zero.</param>
/// <param name="CommissionMode">How the commission is settled (<see cref="ChannelCommissionMode"/>); null where it does not apply (not a channel).</param>
/// <param name="StaysWithoutCommission">The stays whose cost was not reported, counted apart.</param>
/// <param name="MostExpensive">The row with the highest cost rate (never a rate of zero, never a row without one).</param>
public sealed record ChannelCostRow(
    BookingSource? Source,
    ChannelCostKind? Kind,
    int StayCount,
    decimal Gross,
    decimal? Commission,
    decimal? Net,
    decimal? CostRate,
    ChannelCommissionMode? CommissionMode,
    int StaysWithoutCommission,
    bool MostExpensive);

public sealed record ChannelCostsReport(HostDashboardPeriod Period, IReadOnlyList<ChannelCostRow> Rows, ChannelCostRow Total);

/// <summary>A confirmed stay of the period, reduced to what the costs need of it.</summary>
/// <param name="BasePrice">Lodging plus cleaning, tourist tax excluded (<see cref="Booking.BasePrice"/>).</param>
/// <param name="CheckIn">Check-in date, midnight UTC of the stay date.</param>
/// <param name="CheckOut">Check-out date, midnight UTC of the stay date.</param>
/// <param name="Cost">What the stay costs the host in all, as reported; null when nobody reported it (never zero). Zero only where no one charges, or where what a direct stay collected came without a card.</param>
/// <param name="ReportedNet">The net amount the channel manager reported for the stay, if any.</param>
public sealed record ChannelCostStay(
    BookingSource Source,
    decimal BasePrice,
    DateTime CheckIn,
    DateTime CheckOut,
    decimal? Cost,
    decimal? ReportedNet);

public interface IChannelCommissionSettingsProvider
{
    /// <summary>
    /// The mode of each channel of <paramref name="orgId"/> whose portal settings say how its commission is settled; a channel that is not in the answer is
    /// <see cref="ChannelCommissionMode.Unknown"/> (the portal is not in the cache, it maps to no channel, its settings say both or neither, or the org is not
    /// connected to Octorate).
    /// </summary>
    Task<IReadOnlyDictionary<BookingSource, ChannelCommissionMode>> GetModesAsync(Guid orgId, CancellationToken cancellationToken = default);
}

public static class ChannelCostRules
{
    public static ChannelCostKind KindOf(BookingSource source)
    {
        if (FiscalCopy.IsOtaBookingSource(source))
            return ChannelCostKind.ChannelCommission;

        return source == BookingSource.Direct ? ChannelCostKind.PaymentFee : ChannelCostKind.NoCost;
    }

    public static ChannelCommissionMode ModeOf(bool commissionIncludedInPayments, bool? commissionsInvoiced)
    {
        if (commissionIncludedInPayments && commissionsInvoiced != true)
            return ChannelCommissionMode.DeductedFromPayout;

        if (!commissionIncludedInPayments && commissionsInvoiced == true)
            return ChannelCommissionMode.InvoicedSeparately;

        return ChannelCommissionMode.Unknown;
    }

    public static ChannelCostsReport Report(
        HostDashboardPeriod period,
        IReadOnlyCollection<ChannelCostStay> stays,
        IReadOnlyDictionary<BookingSource, ChannelCommissionMode> modes)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(stays);
        ArgumentNullException.ThrowIfNull(modes);

        var rows = stays
            .GroupBy(s => s.Source)
            .Select(g => RowOf(g.Key, g.ToList(), period, modes))
            .Where(r => r.StayCount > 0)
            .OrderBy(r => r.CostRate is null ? 1 : 0)
            .ThenByDescending(r => r.CostRate)
            .ThenByDescending(r => r.Gross)
            .ThenBy(r => r.Source)
            .ToList();

        if (rows.Count > 0 && rows[0].CostRate is > 0m)
            rows[0] = rows[0] with { MostExpensive = true };

        var all = FiguresOf(stays, period.From, period.To);
        var total = new ChannelCostRow(null, null, all.StayCount, all.Gross, all.Commission, all.Net, all.CostRate, null, all.StaysWithoutCommission, false);
        return new ChannelCostsReport(period, rows, total);
    }

    private static ChannelCostRow RowOf(
        BookingSource source,
        IReadOnlyCollection<ChannelCostStay> stays,
        HostDashboardPeriod period,
        IReadOnlyDictionary<BookingSource, ChannelCommissionMode> modes)
    {
        var figures = FiguresOf(stays, period.From, period.To);
        var kind = KindOf(source);
        ChannelCommissionMode? mode = kind == ChannelCostKind.ChannelCommission
            ? modes.GetValueOrDefault(source, ChannelCommissionMode.Unknown)
            : null;
        return new ChannelCostRow(
            source, kind, figures.StayCount, figures.Gross, figures.Commission, figures.Net, figures.CostRate, mode, figures.StaysWithoutCommission, false);
    }

    private static CostFigures FiguresOf(IReadOnlyCollection<ChannelCostStay> stays, DateTime from, DateTime to)
    {
        var counted = stays.Where(s => PropertyOccupancy.NightsIn(s.CheckIn, s.CheckOut, from, to).Any()).ToList();
        var gross = StayKpiRules.RevenueOf(counted.Select(s => new HostRevenueStay(s.BasePrice, s.CheckIn, s.CheckOut, s.Source)), from, to);

        var commission = 0m;
        var reportedGross = 0m;
        var net = 0m;
        var anyCost = false;
        var anyNet = false;
        var without = 0;
        foreach (var stay in counted)
        {
            var grossShare = StayKpiRules.RevenueInPeriod(stay.BasePrice, stay.CheckIn, stay.CheckOut, from, to);
            if (stay.Cost is { } cost)
            {
                var costShare = StayKpiRules.RevenueInPeriod(cost, stay.CheckIn, stay.CheckOut, from, to);
                commission += costShare;
                reportedGross += grossShare;
                anyCost = true;
                if (stay.ReportedNet is null)
                {
                    net += grossShare - costShare;
                    anyNet = true;
                }
            }
            else
            {
                without++;
            }

            if (stay.ReportedNet is { } reported)
            {
                net += StayKpiRules.RevenueInPeriod(reported, stay.CheckIn, stay.CheckOut, from, to);
                anyNet = true;
            }
        }

        return new CostFigures(
            counted.Count,
            gross,
            anyCost ? Money(commission) : null,
            anyNet ? Money(net) : null,
            anyCost && reportedGross > 0m ? Math.Round(commission / reportedGross, 4, MidpointRounding.AwayFromZero) : null,
            without);
    }

    private static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private sealed record CostFigures(int StayCount, decimal Gross, decimal? Commission, decimal? Net, decimal? CostRate, int StaysWithoutCommission);
}
