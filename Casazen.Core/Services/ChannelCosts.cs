using Casazen.Core.Entities;

namespace Casazen.Core.Services;

// The channel costs report of the host (NF-98, E07/F03/US02/T02): what each channel costs, by accrual like the revenue of the Home. The gross is the Home's
// revenue of the stays; the commission and the net are what the channel manager stored on the imported bookings, pro rata per night as the revenue is; the
// processing fees of the direct stays are the ones the card provider reported. A cost nobody reported is "not available", never zero.

/// <summary>How the commission of a channel is settled, from the settings of its portal (<c>commissionIncludedInPayments</c>, <c>commissionsInvoiced</c>).</summary>
public enum ChannelCommissionMode
{
    /// <summary>The channel pays the host net of its commission: the commission is taken out of the payout.</summary>
    DeductedFromPayout,

    /// <summary>The channel invoices its commission to the host afterwards: the host pays it apart.</summary>
    InvoicedSeparately,

    /// <summary>The portal settings cannot be read, the channel has no portal there, or they say both (or neither): nothing is claimed.</summary>
    Unknown,
}

/// <summary>What the cost of a channel is.</summary>
public enum ChannelCostKind
{
    /// <summary>The commission of the channel (<see cref="Booking.OtaCommission"/>, as the channel manager reports it).</summary>
    ChannelCommission,

    /// <summary>The processing fees of the card payments of the direct stays (<see cref="Payment.ProviderFeeAmount"/>, as the card provider reported them).</summary>
    PaymentFee,

    /// <summary>No one charges the host: a stay the host entered, or a local one.</summary>
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

/// <summary>What each channel costs in a period (NF-98).</summary>
/// <param name="Period">The days of the report (Europe/Rome, both included).</param>
/// <param name="Rows">One row for each channel with a stay in the period, by cost rate, the highest first, the rows without a rate last.</param>
/// <param name="Total">Every channel together: the Home's revenue of the same days as its gross, the costs that are known as its commission.</param>
public sealed record ChannelCostsReport(HostDashboardPeriod Period, IReadOnlyList<ChannelCostRow> Rows, ChannelCostRow Total);

/// <summary>A confirmed stay of the period, reduced to what the costs need of it.</summary>
/// <param name="BasePrice">Lodging plus cleaning, tourist tax excluded (<see cref="Booking.BasePrice"/>).</param>
/// <param name="CheckIn">Check-in date, midnight UTC of the stay date.</param>
/// <param name="CheckOut">Check-out date, midnight UTC of the stay date.</param>
/// <param name="Cost">What the stay costs the host in all, as reported; null when nobody reported it (never zero). Zero only where no one charges.</param>
/// <param name="ReportedNet">The net amount the channel manager reported for the stay, if any.</param>
public sealed record ChannelCostStay(
    BookingSource Source,
    decimal BasePrice,
    DateTime CheckIn,
    DateTime CheckOut,
    decimal? Cost,
    decimal? ReportedNet);

/// <summary>
/// How each channel settles its commission, from the cached settings of the portals (E09/F01/US01/T02) and the same mapping of a portal to a channel the booking
/// import uses. Reads CasaZen's own database: nothing is asked of Octorate.
/// </summary>
public interface IChannelCommissionSettingsProvider
{
    /// <summary>
    /// The mode of each channel of <paramref name="orgId"/> whose portal settings say how its commission is settled; a channel that is not in the answer is
    /// <see cref="ChannelCommissionMode.Unknown"/> (the portal is not in the cache, it maps to no channel, its settings say both or neither, or the org is not
    /// connected to Octorate).
    /// </summary>
    Task<IReadOnlyDictionary<BookingSource, ChannelCommissionMode>> GetModesAsync(Guid orgId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The rules of the channel costs report (NF-98), written once: what kind of cost a channel has, how a portal's settings become a mode, and how the figures of a
/// channel are worked out from its stays. Nothing here reads a database or a clock.
/// </summary>
public static class ChannelCostRules
{
    /// <summary>
    /// The kind of cost of the stays of <paramref name="source"/>: a channel (the one definition of the code, <see cref="FiscalCopy.IsOtaBookingSource"/>) charges a
    /// commission, the booking site (<see cref="BookingSource.Direct"/>) the fees of its card payments, the others nothing.
    /// </summary>
    public static ChannelCostKind KindOf(BookingSource source)
    {
        if (FiscalCopy.IsOtaBookingSource(source))
            return ChannelCostKind.ChannelCommission;

        return source == BookingSource.Direct ? ChannelCostKind.PaymentFee : ChannelCostKind.NoCost;
    }

    /// <summary>
    /// The mode of a portal from its two flags: included in the payments (the channel pays the host net, <see cref="ChannelCommissionMode.DeductedFromPayout"/>) or
    /// invoiced (the host pays the channel afterwards, <see cref="ChannelCommissionMode.InvoicedSeparately"/>). Only a flag that is true counts: an absent one is not
    /// a "no", and two that disagree claim nothing, so <see cref="ChannelCommissionMode.Unknown"/> is the answer for anything else.
    /// </summary>
    public static ChannelCommissionMode ModeOf(bool commissionIncludedInPayments, bool? commissionsInvoiced)
    {
        if (commissionIncludedInPayments && commissionsInvoiced != true)
            return ChannelCommissionMode.DeductedFromPayout;

        if (!commissionIncludedInPayments && commissionsInvoiced == true)
            return ChannelCommissionMode.InvoicedSeparately;

        return ChannelCommissionMode.Unknown;
    }

    /// <summary>
    /// What each channel costs over <paramref name="period"/>: one row for each channel of <paramref name="stays"/>, ordered by cost rate (the highest first, the rows
    /// without a rate last, then the larger gross), the most expensive marked, and the total over all the stays at once (so its gross is the Home's revenue of those
    /// days, which can differ by a cent from the sum of the rounded rows).
    /// </summary>
    /// <param name="stays">The confirmed stays with a night in the period.</param>
    /// <param name="modes">How each channel settles its commission (<see cref="IChannelCommissionSettingsProvider"/>); a channel that is missing is unknown.</param>
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

        // The rows are ordered by rate, so the first one has the highest; a rate of zero is not the most expensive of anything.
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

    // The figures of a set of stays from <from> (included) to <to> (excluded). Every share of a stay is pro rata per night in the period, as the revenue is
    // (StayKpiRules.RevenueInPeriod), summed unrounded and rounded once on the total.
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
