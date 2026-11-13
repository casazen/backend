using System.Globalization;
using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

// The collections report of the host (NF-97, E07/F03/US02/T01): what came in by date, by payment method and by channel, the balances still to
// collect and the credits. By cash, like the Home's "incassato": the total of the report is the number the Home gives for the same days.

// ---------------------------------------------------------------- what came in

/// <summary>The unit of the buckets of the collections by date.</summary>
public enum HostCollectionsBucketUnit
{
    /// <summary>One bucket for each day: a period of up to <see cref="HostCollectionsRules.MaxDailyDays"/> days.</summary>
    Day,

    /// <summary>One bucket for each calendar month, the first and the last cut at the edges of the period: a longer period.</summary>
    Month,
}

/// <summary>The money collected in one day or one month of the period.</summary>
/// <param name="Label">A stable key, never text to show: the day (<c>2026-10-09</c>) or the month (<c>2026-10</c>).</param>
/// <param name="From">First day of the bucket, a Europe/Rome calendar date.</param>
/// <param name="To">Last day of the bucket, included (the day itself for a day bucket).</param>
/// <param name="Amount">Euros, rounded to the cent, net of what was refunded (a day without payments is a real zero).</param>
/// <param name="PaymentCount">The payments collected in it.</param>
public sealed record HostCollectionsBucket(
    HostCollectionsBucketUnit Unit,
    string Label,
    DateOnly From,
    DateOnly To,
    decimal Amount,
    int PaymentCount);

/// <summary>The money collected with one payment method (<see cref="Payment.Method"/>).</summary>
/// <param name="Share">Of the total collected, from 0 to 1 to four decimals; null when the total is zero (a share of nothing says nothing).</param>
public sealed record HostCollectionsMethodRow(PaymentMethod Method, decimal Amount, int PaymentCount, decimal? Share);

/// <summary>The money collected for the stays of one channel (<see cref="Booking.Source"/> of the booking of the payment).</summary>
/// <param name="Share">Of the total collected, from 0 to 1 to four decimals; null when the total is zero.</param>
public sealed record HostCollectionsChannelRow(BookingSource Source, decimal Amount, int PaymentCount, decimal? Share);

/// <summary>
/// A payment the report counts as collected (<see cref="PaymentCashRules.CollectedBetweenInstants"/>), reduced to what the report needs of it.
/// </summary>
/// <param name="CollectedAt">The instant it came in (UTC): <see cref="Payment.ProcessedAt"/>, and <see cref="Payment.CreatedAt"/> for a payment that has none.</param>
/// <param name="Amount">The amount of the payment.</param>
/// <param name="RefundedAmount">What was refunded of it.</param>
/// <param name="Method">How it was paid.</param>
/// <param name="Source">The channel of the booking it pays.</param>
public sealed record HostCollectedPayment(DateTime CollectedAt, decimal Amount, decimal RefundedAmount, PaymentMethod Method, BookingSource Source)
{
    /// <summary>What the payment brings: its amount minus what was refunded (<see cref="PaymentCashRules.Collected"/>).</summary>
    public decimal Collected => PaymentCashRules.Collected(Amount, RefundedAmount);
}

/// <summary>The figures of the money collected in a period, cut three ways (<see cref="HostCollectionsRules.Summarize"/>).</summary>
public sealed record HostCollectionsFigures(
    HostDashboardCollected Total,
    IReadOnlyList<HostCollectionsBucket> ByDate,
    IReadOnlyList<HostCollectionsMethodRow> ByMethod,
    IReadOnlyList<HostCollectionsChannelRow> ByChannel);

// ---------------------------------------------------------------- what reached the account

/// <summary>Whether the day the provider credits the funds has come.</summary>
public enum HostCreditStatus
{
    /// <summary>The credit day is still ahead: the money is on its way.</summary>
    Expected,

    /// <summary>The credit day has come (today or before): the provider has credited the funds.</summary>
    Credited,
}

/// <summary>
/// The card payments the provider credited on one day, in one currency: the payments that share the day the provider reported for the funds
/// (<see cref="Payment.ProviderAvailableOn"/>). The payout the bank sees is the provider's own and is not stored, so the day is the arrival date.
/// </summary>
/// <param name="ArrivalDate">The day the funds are credited (the date the provider gave, UTC).</param>
/// <param name="Currency">The currency of the balance the provider credited.</param>
/// <param name="Amount">What was credited after the provider's commission, summed (<see cref="Payment.ProviderNetAmount"/>).</param>
/// <param name="PaymentCount">The payments credited that day.</param>
public sealed record HostStripeCreditRow(DateOnly ArrivalDate, string Currency, decimal Amount, int PaymentCount, HostCreditStatus Status);

/// <summary>
/// What the card provider credited in the period (<see cref="HostStripeSettlement"/>). Only what the capture stored is here, and a payment whose figures were
/// not captured is never counted as zero: it is told apart in <see cref="PaymentsWithoutFigures"/>.
/// </summary>
/// <param name="Available">False when no payment has stored figures to show for the period: the page says that the figures are not available.</param>
/// <param name="Payouts">The credits by arrival day (and currency), oldest first.</param>
/// <param name="CardPaymentCount">The card payments collected in the period.</param>
/// <param name="PaymentsWithoutFigures">Of them, the payments the capture has not stored a net amount and a credit day for (yet).</param>
public sealed record HostStripeCredits(
    bool Available,
    IReadOnlyList<HostStripeCreditRow> Payouts,
    int CardPaymentCount,
    int PaymentsWithoutFigures);

/// <summary>The stored credit of one card payment: the net amount, the day it is credited and the currency of the balance.</summary>
public sealed record HostStripeSettlement(DateTime AvailableOn, decimal Net, string? Currency);

/// <summary>What the channels of the channel manager credited for the stays of one channel, as the channel manager reports it.</summary>
/// <param name="Currency">The currency of the stays (their property).</param>
/// <param name="Amount">The net amount of the stays the channel collected and reported a net for (<see cref="Booking.OtaNetAmount"/>), summed.</param>
/// <param name="StayCount">The stays that make it.</param>
public sealed record HostChannelCreditRow(BookingSource Source, string Currency, decimal Amount, int StayCount);

/// <summary>
/// The credits of the channels (NF-97): what the channel manager reports as net for the stays the channel collected itself. Reported, never a bank credit
/// and without a date (the import carries none).
/// </summary>
/// <param name="Available">False when no stay has a reported net amount: the figure is not available, never zero.</param>
/// <param name="Rows">One row for each channel (and currency) with a reported net amount, the largest first.</param>
/// <param name="StaysWithoutNet">The stays the channel collected for which the channel manager reported no net amount.</param>
public sealed record HostChannelCredits(bool Available, IReadOnlyList<HostChannelCreditRow> Rows, int StaysWithoutNet);

/// <summary>A stay the channel collected the money of, with the net amount the channel manager reported for it, if any.</summary>
public sealed record HostChannelCreditStay(BookingSource Source, string? Currency, decimal? Net);

/// <summary>The credits of the period: the card provider's and the channels'.</summary>
public sealed record HostCredits(HostStripeCredits Stripe, HostChannelCredits Channels);

/// <summary>The money collected in a period by date, by payment method and by channel, and what reached the account.</summary>
/// <param name="Period">The days of the report (Europe/Rome, both included).</param>
/// <param name="Total">The money collected: the Home's "incassato" for the same days (amount net of refunds, rounded once on the sum, and the count).</param>
/// <param name="ByDate">One bucket for each day (up to <see cref="HostCollectionsRules.MaxDailyDays"/> days) or month of the period, all of them.</param>
/// <param name="ByMethod">The payment methods that collected something, the largest first.</param>
/// <param name="ByChannel">The channels whose stays collected something, the largest first.</param>
/// <param name="Credits">What the card provider and the channels credited in the period.</param>
public sealed record HostCollectionsReport(
    HostDashboardPeriod Period,
    HostDashboardCollected Total,
    IReadOnlyList<HostCollectionsBucket> ByDate,
    IReadOnlyList<HostCollectionsMethodRow> ByMethod,
    IReadOnlyList<HostCollectionsChannelRow> ByChannel,
    HostCredits Credits);

// ---------------------------------------------------------------- what is still to collect

/// <summary>What the balances to collect ask of <see cref="IHostCollectionsReportService.GetBalancesToCollectAsync"/>.</summary>
/// <param name="PropertyId">Only this property (it must be one the scope reaches); null = every property of the scope.</param>
public sealed record HostBalancesQuery(Guid? PropertyId = null);

/// <summary>Where the figures of a balance come from.</summary>
public enum HostBalanceBasis
{
    /// <summary>CasaZen's own money: the total of the booking minus the payments collected for it.</summary>
    Payments,

    /// <summary>The channel manager's: the balance Octorate reported for a reservation (<see cref="Booking.OtaBalanceAmount"/>), stored as read.</summary>
    ChannelManager,
}

/// <summary>
/// A stay with something still to collect. A money report never carries a guest: no name, no contact, nothing that says who the guest is.
/// </summary>
/// <param name="BookingCode">The code in its stored form (<see cref="BookingCodes.Format"/> shows it as <c>XXXXX-XXXXX</c>).</param>
/// <param name="Source">The channel of the booking.</param>
/// <param name="CheckIn">The check-in day, a Europe/Rome calendar date.</param>
/// <param name="CheckOut">The check-out day, a Europe/Rome calendar date.</param>
/// <param name="Currency">The currency of the property: every amount of the row is in it.</param>
/// <param name="Total">What the stay costs; null for a channel reservation Octorate gave no price for.</param>
/// <param name="Collected">What came in; null for a channel reservation Octorate did not say the paid amount of (never derived).</param>
/// <param name="ToCollect">What is still to collect, always above zero.</param>
/// <param name="Overdue">The check-out day is already past (Europe/Rome).</param>
public sealed record HostBalanceRow(
    Guid BookingId,
    string BookingCode,
    Guid PropertyId,
    string PropertyName,
    BookingSource Source,
    DateOnly CheckIn,
    DateOnly CheckOut,
    string Currency,
    decimal? Total,
    decimal? Collected,
    decimal ToCollect,
    bool Overdue,
    HostBalanceBasis Basis);

/// <summary>What is to collect in one currency: amounts of different currencies are never added together.</summary>
public sealed record HostBalanceTotal(string Currency, decimal Amount, int StayCount);

/// <summary>The balances still to collect, by check-in, with the total to collect.</summary>
/// <param name="AsOf">The day the balances are read (today in Europe/Rome): what "overdue" is judged on.</param>
/// <param name="Rows">The stays with an amount to collect, by check-in then code.</param>
/// <param name="Totals">What is to collect, one entry for each currency of the rows.</param>
/// <param name="Since">The first check-out day listed (<see cref="HostCollectionsRules.BalancesSince"/>): a stay that ended before it is not in the report.</param>
public sealed record HostBalancesToCollect(DateOnly AsOf, IReadOnlyList<HostBalanceRow> Rows, IReadOnlyList<HostBalanceTotal> Totals, DateOnly? Since = null);

// ---------------------------------------------------------------- the rules

/// <summary>
/// The rules of the collections report (NF-97), written once: the buckets, the shares, the credits and what makes a balance. Nothing here reads a database or a
/// clock; the service gives it rows and today.
/// </summary>
public static class HostCollectionsRules
{
    /// <summary>
    /// A period of up to this many days is cut by day, a longer one by month (a day of a year of days is a table nobody reads): the limit of the trends of
    /// the reports (<see cref="HostReportRules.MaxWeeklyTrendDays"/>).
    /// </summary>
    public const int MaxDailyDays = HostReportRules.MaxWeeklyTrendDays;

    /// <summary>
    /// The channels, as the one definition of the code says (<see cref="FiscalCopy.IsOtaBookingSource"/>): a booking of these is the channel's, never a
    /// stay whose price the host entered. Derived from the enum, never written out, so a new channel is one here as soon as the definition says so.
    /// </summary>
    public static IReadOnlyList<BookingSource> ChannelSources { get; } =
        Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToArray();

    private static readonly BookingSource[] ChannelSourceArray = [.. ChannelSources];

    /// <summary>Day for a period of up to <see cref="MaxDailyDays"/> days, month for a longer one.</summary>
    public static HostCollectionsBucketUnit UnitFor(HostDashboardPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return period.Nights <= MaxDailyDays ? HostCollectionsBucketUnit.Day : HostCollectionsBucketUnit.Month;
    }

    /// <summary>
    /// The buckets of <paramref name="period"/> before any money is in them, in order, covering it exactly: a bucket for each day, or for each month with the
    /// first and the last cut at the edges of the period (the cut of the Home's chart, <see cref="HostOverviewSeriesRules.BucketsOf"/>).
    /// </summary>
    public static IReadOnlyList<HostCollectionsBucket> EmptyBucketsOf(HostDashboardPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        var unit = UnitFor(period);
        var buckets = new List<HostCollectionsBucket>();
        if (unit == HostCollectionsBucketUnit.Day)
        {
            var last = DateOnly.FromDateTime(period.To);
            for (var day = DateOnly.FromDateTime(period.From); day < last; day = day.AddDays(1))
                buckets.Add(new HostCollectionsBucket(unit, LabelOf(day, unit), day, day, 0m, 0));

            return buckets;
        }

        foreach (var span in HostOverviewSeriesRules.BucketsOf(period, HostOverviewSeriesUnit.Month))
        {
            buckets.Add(new HostCollectionsBucket(
                unit, span.Label, DateOnly.FromDateTime(span.From), DateOnly.FromDateTime(span.To.AddDays(-1)), 0m, 0));
        }

        return buckets;
    }

    /// <summary>
    /// The money collected, cut by date, by payment method and by channel. Each payment counts for what it brings (the amount minus what was refunded) on the
    /// Europe/Rome day it came in; the total is the Home's (<see cref="HostDashboardCollected"/>: the sum rounded once, and the count of the payments), and each
    /// cut adds up to it.
    /// </summary>
    /// <param name="payments">The payments that count as collected in the period (<see cref="PaymentCashRules.CollectedBetweenInstants"/>), nothing else.</param>
    /// <exception cref="InvalidOperationException">A payment came in on a day the period does not have: the rows are not the ones of the period.</exception>
    public static HostCollectionsFigures Summarize(HostDashboardPeriod period, IReadOnlyCollection<HostCollectedPayment> payments)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(payments);

        var sums = new HostCollectionsSums(period);
        foreach (var payment in payments)
            sums.Add(payment);

        return sums.Figures();
    }

    /// <summary>
    /// The credits of the card provider: the stored credits of the payments grouped by the day the funds arrive and the currency, oldest first, each
    /// <see cref="HostCreditStatus.Credited"/> when its day is <paramref name="today"/> or before and <see cref="HostCreditStatus.Expected"/> after. Without a
    /// credit to show it is not available (<see cref="HostStripeCredits.Available"/> false), never a zero.
    /// </summary>
    public static HostStripeCredits StripeCreditsOf(
        IEnumerable<HostStripeSettlement> settlements, DateOnly today, int cardPaymentCount, int paymentsWithoutFigures)
    {
        ArgumentNullException.ThrowIfNull(settlements);

        var rows = settlements
            .GroupBy(s => (Day: DateOnly.FromDateTime(s.AvailableOn), Currency: StayCurrency.Normalize(s.Currency)))
            .Select(g => new HostStripeCreditRow(
                g.Key.Day, g.Key.Currency, Money(g.Sum(s => s.Net)), g.Count(), CreditStatusOf(g.Key.Day, today)))
            .OrderBy(r => r.ArrivalDate)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();
        return new HostStripeCredits(rows.Count > 0, rows, cardPaymentCount, paymentsWithoutFigures);
    }

    /// <summary>The funds are credited when their day is today or before; later they are expected.</summary>
    public static HostCreditStatus CreditStatusOf(DateOnly arrivalDate, DateOnly today) =>
        arrivalDate <= today ? HostCreditStatus.Credited : HostCreditStatus.Expected;

    /// <summary>
    /// The credits of the channels: the net amounts the channel manager reported for the stays the channel collected, by channel and currency, the largest
    /// first. A stay without a reported net is only counted (<see cref="HostChannelCredits.StaysWithoutNet"/>); with none reported the credits are not
    /// available, never zero.
    /// </summary>
    public static HostChannelCredits ChannelCreditsOf(IEnumerable<HostChannelCreditStay> stays)
    {
        ArgumentNullException.ThrowIfNull(stays);

        var list = stays.ToList();
        var rows = list
            .Where(s => s.Net is not null)
            .GroupBy(s => (s.Source, Currency: StayCurrency.Normalize(s.Currency)))
            .Select(g => new HostChannelCreditRow(g.Key.Source, g.Key.Currency, Money(g.Sum(s => s.Net!.Value)), g.Count()))
            .OrderByDescending(r => r.Amount)
            .ThenBy(r => r.Source)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();
        return new HostChannelCredits(rows.Count > 0, rows, list.Count(s => s.Net is null));
    }

    /// <summary>The check-out day is before today: the stay is over and something is still to collect.</summary>
    public static bool IsOverdue(DateOnly checkOut, DateOnly today) => checkOut < today;

    /// <summary>How far back the balances to collect look, in calendar months: the horizon of every report (<see cref="HostReportRules.MaxMonths"/>).</summary>
    public const int BalancesMonths = HostReportRules.MaxMonths;

    /// <summary>
    /// The first check-out day the balances to collect list: <see cref="BalancesMonths"/> months before <paramref name="today"/>. A stay that ended before it is out of the
    /// horizon of the report, so the list does not grow with the whole history of the host (a stay never marked as paid stays overdue for ever).
    /// </summary>
    public static DateOnly BalancesSince(DateOnly today) => today.AddMonths(-BalancesMonths);

    /// <summary>What is still to collect of a stay CasaZen holds the money of: the total minus what was collected, never below zero.</summary>
    public static decimal ToCollect(decimal total, decimal collected) => Math.Max(0m, total - collected);

    /// <summary>The balances added up, by currency (the amounts of two currencies are never added), by currency code.</summary>
    public static IReadOnlyList<HostBalanceTotal> TotalsOf(IEnumerable<HostBalanceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .Select(g => new HostBalanceTotal(g.Key, Money(g.Sum(r => r.ToCollect)), g.Count()))
            .OrderBy(t => t.Currency, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A booking whose money is the channel manager's, as a query can ask it: the one definition of <see cref="BookingAmountSourceRules.HoldsChannelManagerMoney"/>
    /// written as an expression (a booking of a channel with its reservation on Octorate, or a reservation of the booking engine of the agency that the import
    /// made: <see cref="BookingSource.Direct"/>, a reservation, and no row of the mirror, which only CasaZen's own push makes). A test holds the two together.
    /// </summary>
    /// <param name="mirrors">The rows of the mirror of the bookings on Octorate (<c>OctorateBookingSyncs</c>).</param>
    public static Expression<Func<Booking, bool>> HoldsChannelManagerMoney(IQueryable<OctorateBookingSync> mirrors)
    {
        ArgumentNullException.ThrowIfNull(mirrors);

        var channels = ChannelSourceArray;
        return b => !string.IsNullOrWhiteSpace(b.OctorateReservationId)
                    && (channels.Contains(b.Source) || (b.Source == BookingSource.Direct && !mirrors.Any(s => s.BookingId == b.Id)));
    }

    /// <summary>
    /// A booking whose money is CasaZen's: not one of a channel (<see cref="ChannelSources"/>: a portal collects the money itself, the host records no receipt
    /// on it) and not one whose money is the channel manager's (<see cref="HoldsChannelManagerMoney"/>). Its balance is the total minus the payments
    /// collected for it.
    /// </summary>
    /// <param name="mirrors">The rows of the mirror of the bookings on Octorate.</param>
    public static Expression<Func<Booking, bool>> HoldsCasaZenMoney(IQueryable<OctorateBookingSync> mirrors)
    {
        ArgumentNullException.ThrowIfNull(mirrors);

        var channels = ChannelSourceArray;
        return b => !channels.Contains(b.Source)
                    && !(!string.IsNullOrWhiteSpace(b.OctorateReservationId) && b.Source == BookingSource.Direct && !mirrors.Any(s => s.BookingId == b.Id));
    }

    /// <summary>The key of a day in its bucket: the day (<c>2026-10-09</c>) or the month (<c>2026-10</c>).</summary>
    public static string LabelOf(DateOnly day, HostCollectionsBucketUnit unit) =>
        day.ToString(unit == HostCollectionsBucketUnit.Day ? "yyyy-MM-dd" : "yyyy-MM", CultureInfo.InvariantCulture);

    internal static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    internal static decimal? ShareOf(decimal part, decimal whole) =>
        whole <= 0m ? null : Math.Round(part / whole, 4, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The money collected in a period, added up one payment at a time (<see cref="HostCollectionsRules.Summarize"/> is this over a list): the report never holds the
/// payments of the period, only the sums of its cuts (a bucket for each day or month, each payment method, each channel), so a period with many payments needs the
/// memory of one with a few.
/// </summary>
public sealed class HostCollectionsSums
{
    private readonly HostCollectionsBucketUnit _unit;
    private readonly IReadOnlyList<HostCollectionsBucket> _empty;
    private readonly Dictionary<string, int> _indexOf;
    private readonly decimal[] _amounts;
    private readonly int[] _counts;
    private readonly Dictionary<PaymentMethod, (decimal Amount, int Count)> _methods = new();
    private readonly Dictionary<BookingSource, (decimal Amount, int Count)> _channels = new();
    private decimal _total;
    private int _count;

    public HostCollectionsSums(HostDashboardPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        _unit = HostCollectionsRules.UnitFor(period);
        _empty = HostCollectionsRules.EmptyBucketsOf(period);
        _indexOf = new Dictionary<string, int>(_empty.Count, StringComparer.Ordinal);
        for (var index = 0; index < _empty.Count; index++)
            _indexOf[_empty[index].Label] = index;

        _amounts = new decimal[_empty.Count];
        _counts = new int[_empty.Count];
    }

    /// <summary>Adds a payment that came in on a day of the period: it counts for what it brings, on the Europe/Rome day it came in.</summary>
    /// <exception cref="InvalidOperationException">The payment came in on a day the period does not have: the rows are not the ones of the period.</exception>
    public void Add(HostCollectedPayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var label = HostCollectionsRules.LabelOf(RomeCalendar.DateInRome(payment.CollectedAt), _unit);
        if (!_indexOf.TryGetValue(label, out var index))
            throw new InvalidOperationException($"A payment collected on {label} is outside the period of the report.");

        var collected = payment.Collected;
        _amounts[index] += collected;
        _counts[index]++;
        _methods[payment.Method] = Plus(_methods.GetValueOrDefault(payment.Method), collected);
        _channels[payment.Source] = Plus(_channels.GetValueOrDefault(payment.Source), collected);
        _total += collected;
        _count++;
    }

    /// <summary>The figures of what was added: the total is the sum rounded once with the count of the payments, and each cut adds up to it.</summary>
    public HostCollectionsFigures Figures()
    {
        var total = HostCollectionsRules.Money(_total);
        var byDate = _empty
            .Select((bucket, index) => bucket with { Amount = HostCollectionsRules.Money(_amounts[index]), PaymentCount = _counts[index] })
            .ToList();
        var byMethod = _methods
            .Select(m => new HostCollectionsMethodRow(
                m.Key, HostCollectionsRules.Money(m.Value.Amount), m.Value.Count, HostCollectionsRules.ShareOf(m.Value.Amount, total)))
            .OrderByDescending(r => r.Amount)
            .ThenBy(r => r.Method)
            .ToList();
        var byChannel = _channels
            .Select(c => new HostCollectionsChannelRow(
                c.Key, HostCollectionsRules.Money(c.Value.Amount), c.Value.Count, HostCollectionsRules.ShareOf(c.Value.Amount, total)))
            .OrderByDescending(r => r.Amount)
            .ThenBy(r => r.Source)
            .ToList();
        return new HostCollectionsFigures(new HostDashboardCollected(total, _count), byDate, byMethod, byChannel);
    }

    private static (decimal Amount, int Count) Plus((decimal Amount, int Count) sum, decimal amount) => (sum.Amount + amount, sum.Count + 1);
}
