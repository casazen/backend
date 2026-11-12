using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The collections report of the host (NF-97, <c>GET /api/reports/collections</c>): the money that came in, by cash, by date, by payment method and by
/// channel, and what reached the account. The total is the Home's "incassato" for the same days. Amounts are in euros to the cent and also in cents
/// (exact). Definitions in <see cref="IHostCollectionsReportService"/> and <c>docs/TECHNICAL.md</c> ("Reports").
/// </summary>
public sealed class CollectionsReportDto
{
    /// <summary>The name of the report with its base (Italian, the one of the file).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>cash</c>: the day the money came in, not the nights of the stay it pays; refunds are deducted.</summary>
    public string Basis { get; init; } = "cash";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The Home's "incassato" for the same days: what every payment brings (its amount minus what was refunded), rounded once on the sum.</summary>
    public CollectionsTotalDto Total { get; init; } = new();

    /// <summary>One bucket for each day (a period of up to 92 days) or month (longer), every one of them: a day without payments is a zero.</summary>
    public IReadOnlyList<CollectionsBucketDto> ByDate { get; init; } = [];

    /// <summary>The payment methods that collected something, the largest first.</summary>
    public IReadOnlyList<CollectionsMethodRowDto> ByMethod { get; init; } = [];

    /// <summary>The channels whose stays collected something, the largest first.</summary>
    public IReadOnlyList<CollectionsChannelRowDto> ByChannel { get; init; } = [];

    /// <summary>What the card provider and the channels credited in the period: shown where the data exists, "not available" where it does not.</summary>
    public CollectionsCreditsDto Credits { get; init; } = new();

    public static CollectionsReportDto From(HostCollectionsReport report, string title) => new()
    {
        Title = title,
        Period = ReportPeriodDto.Of(report.Period),
        Total = new CollectionsTotalDto
        {
            Amount = report.Total.Amount,
            AmountCents = PaymentCashRules.ToCents(report.Total.Amount),
            PaymentCount = report.Total.PaymentCount,
        },
        ByDate = report.ByDate.Select(CollectionsBucketDto.Of).ToList(),
        ByMethod = report.ByMethod.Select(CollectionsMethodRowDto.From).ToList(),
        ByChannel = report.ByChannel.Select(CollectionsChannelRowDto.From).ToList(),
        Credits = CollectionsCreditsDto.From(report.Credits),
    };
}

/// <summary>The money collected in a period and the payments it is made of.</summary>
public sealed class CollectionsTotalDto
{
    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    /// <summary>The payments collected (one only partly refunded counts for what is left).</summary>
    public int PaymentCount { get; init; }
}

/// <summary>The money collected in one day or one month.</summary>
public sealed class CollectionsBucketDto
{
    /// <summary><c>day</c> or <c>month</c>: the same for every bucket of a report.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>A stable key, never text to show: the day (<c>2026-10-09</c>) or the month (<c>2026-10</c>).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>First day of the bucket (Europe/Rome).</summary>
    public DateOnly From { get; init; }

    /// <summary>Last day of the bucket, included (the same day for a day bucket).</summary>
    public DateOnly To { get; init; }

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }

    public static CollectionsBucketDto Of(HostCollectionsBucket bucket) => new()
    {
        Unit = bucket.Unit == HostCollectionsBucketUnit.Day ? "day" : "month",
        Label = bucket.Label,
        From = bucket.From,
        To = bucket.To,
        Amount = bucket.Amount,
        AmountCents = PaymentCashRules.ToCents(bucket.Amount),
        PaymentCount = bucket.PaymentCount,
    };
}

/// <summary>The money collected with one payment method.</summary>
public sealed class CollectionsMethodRowDto
{
    /// <summary>The name of the payment method: <c>CreditCard</c>, <c>BankTransfer</c>, <c>PayPal</c>, <c>ApplePay</c>, <c>GooglePay</c>, <c>CashOnArrival</c>. A key, never text to show.</summary>
    public string Method { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }

    /// <summary>The part of the total this method collected, from 0 to 1; null when the total is zero.</summary>
    public decimal? Share { get; init; }

    public static CollectionsMethodRowDto From(HostCollectionsMethodRow row) => new()
    {
        Method = row.Method.ToString(),
        Amount = row.Amount,
        AmountCents = PaymentCashRules.ToCents(row.Amount),
        PaymentCount = row.PaymentCount,
        Share = row.Share,
    };
}

/// <summary>The money collected for the stays of one channel.</summary>
public sealed class CollectionsChannelRowDto
{
    /// <summary>The channel, the name of the booking source: <c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c>, ... A key, never text to show.</summary>
    public string Source { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }

    /// <summary>The part of the total this channel collected, from 0 to 1; null when the total is zero.</summary>
    public decimal? Share { get; init; }

    public static CollectionsChannelRowDto From(HostCollectionsChannelRow row) => new()
    {
        Source = row.Source.ToString(),
        Amount = row.Amount,
        AmountCents = PaymentCashRules.ToCents(row.Amount),
        PaymentCount = row.PaymentCount,
        Share = row.Share,
    };
}

/// <summary>What reached the account in the period: the card provider's credits and the channels'.</summary>
public sealed class CollectionsCreditsDto
{
    public StripeCreditsDto Stripe { get; init; } = new();

    public ChannelCreditsDto Channels { get; init; } = new();

    public static CollectionsCreditsDto From(HostCredits credits) => new()
    {
        Stripe = StripeCreditsDto.From(credits.Stripe),
        Channels = ChannelCreditsDto.From(credits.Channels),
    };
}

/// <summary>
/// The credits of the card provider, from the figures stored on the payments. With <see cref="Available"/> false the page says that the figures are not
/// available: a credit that was not stored is never shown as zero.
/// </summary>
public sealed class StripeCreditsDto
{
    /// <summary>False when no payment has stored figures to show for the period.</summary>
    public bool Available { get; init; }

    /// <summary>The credits by arrival day (and currency), oldest first: the payments the provider credits on the same day. The provider's own payout id is not stored.</summary>
    public IReadOnlyList<StripeCreditRowDto> Payouts { get; init; } = [];

    /// <summary>The card payments collected in the period.</summary>
    public int CardPaymentCount { get; init; }

    /// <summary>Of them, the payments whose figures were not stored (yet): they are in no row.</summary>
    public int PaymentsWithoutFigures { get; init; }

    public static StripeCreditsDto From(HostStripeCredits credits) => new()
    {
        Available = credits.Available,
        Payouts = credits.Payouts.Select(StripeCreditRowDto.From).ToList(),
        CardPaymentCount = credits.CardPaymentCount,
        PaymentsWithoutFigures = credits.PaymentsWithoutFigures,
    };
}

/// <summary>The card payments credited on one day, in one currency.</summary>
public sealed class StripeCreditRowDto
{
    /// <summary>The day the funds are credited (the date the provider reported).</summary>
    public DateOnly ArrivalDate { get; init; }

    /// <summary>The currency of the balance that was credited.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>What was credited after the provider's commission, summed.</summary>
    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }

    /// <summary><c>Credited</c> when the arrival day has come, <c>Expected</c> when it is still ahead. A key, never text to show.</summary>
    public string Status { get; init; } = string.Empty;

    public static StripeCreditRowDto From(HostStripeCreditRow row) => new()
    {
        ArrivalDate = row.ArrivalDate,
        Currency = row.Currency,
        Amount = row.Amount,
        AmountCents = PaymentCashRules.ToCents(row.Amount),
        PaymentCount = row.PaymentCount,
        Status = row.Status.ToString(),
    };
}

/// <summary>
/// The credits of the channels: what the channel manager reports as the net amount of the stays the channel collected. Reported, never a bank credit, and
/// without a date. With <see cref="Available"/> false the page says that the figures are not available, never zero.
/// </summary>
public sealed class ChannelCreditsDto
{
    /// <summary>False when no stay has a reported net amount.</summary>
    public bool Available { get; init; }

    /// <summary>Always <c>channelManager</c>: where the figures come from, for the label "reported by the channel manager".</summary>
    public string ReportedBy { get; init; } = "channelManager";

    /// <summary>The channels with a reported net amount, the largest first.</summary>
    public IReadOnlyList<ChannelCreditRowDto> Rows { get; init; } = [];

    /// <summary>The stays the channel collected for which the channel manager reported no net amount.</summary>
    public int StaysWithoutNet { get; init; }

    public static ChannelCreditsDto From(HostChannelCredits credits) => new()
    {
        Available = credits.Available,
        Rows = credits.Rows.Select(ChannelCreditRowDto.From).ToList(),
        StaysWithoutNet = credits.StaysWithoutNet,
    };
}

public sealed class ChannelCreditRowDto
{
    /// <summary>The channel, the name of the booking source. A key, never text to show.</summary>
    public string Source { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    /// <summary>The stays the net amount is the sum of.</summary>
    public int StayCount { get; init; }

    public static ChannelCreditRowDto From(HostChannelCreditRow row) => new()
    {
        Source = row.Source.ToString(),
        Currency = row.Currency,
        Amount = row.Amount,
        AmountCents = PaymentCashRules.ToCents(row.Amount),
        StayCount = row.StayCount,
    };
}

/// <summary>
/// The balances still to collect (NF-97, <c>GET /api/reports/balances-to-collect</c>): the stays whose amount due is above what was collected. A money
/// report never carries a guest: no name, no contact in any row.
/// </summary>
public sealed class BalancesToCollectReportDto
{
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>balances</c>: what is due now on the stays that hold their dates, whatever the day it was due.</summary>
    public string Basis { get; init; } = "balances";

    /// <summary>The day the balances are read (today in Europe/Rome): what <c>overdue</c> is judged on.</summary>
    public DateOnly AsOf { get; init; }

    /// <summary>The stays with an amount to collect, by check-in.</summary>
    public IReadOnlyList<BalanceRowDto> Rows { get; init; } = [];

    /// <summary>What is to collect, one entry for each currency of the rows (amounts of different currencies are never added).</summary>
    public IReadOnlyList<BalanceTotalDto> Totals { get; init; } = [];

    public static BalancesToCollectReportDto From(HostBalancesToCollect report, string title) => new()
    {
        Title = title,
        AsOf = report.AsOf,
        Rows = report.Rows.Select(BalanceRowDto.From).ToList(),
        Totals = report.Totals.Select(BalanceTotalDto.From).ToList(),
    };
}

/// <summary>A stay with something to collect.</summary>
public sealed class BalanceRowDto
{
    public Guid BookingId { get; init; }

    /// <summary>The booking code as the host reads it (<c>XXXXX-XXXXX</c>).</summary>
    public string BookingCode { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    /// <summary>The channel, the name of the booking source. A key, never text to show.</summary>
    public string Source { get; init; } = string.Empty;

    public DateOnly CheckIn { get; init; }

    public DateOnly CheckOut { get; init; }

    /// <summary>The currency of the property: every amount of the row is in it.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>What the stay costs; null for a channel reservation Octorate gave no price for (not available).</summary>
    public decimal? Total { get; init; }

    public long? TotalCents { get; init; }

    /// <summary>What came in; null for a channel reservation Octorate did not report the paid amount of (not available, never derived).</summary>
    public decimal? Collected { get; init; }

    public long? CollectedCents { get; init; }

    /// <summary>What is still to collect: always above zero.</summary>
    public decimal ToCollect { get; init; }

    public long ToCollectCents { get; init; }

    /// <summary>The check-out day is already past.</summary>
    public bool Overdue { get; init; }

    /// <summary>
    /// Where the figures come from: <c>Payments</c> (CasaZen's money: the total minus the payments collected) or <c>ChannelManager</c> (the balance the channel
    /// manager reported). A key, never text to show.
    /// </summary>
    public string Basis { get; init; } = string.Empty;

    public static BalanceRowDto From(HostBalanceRow row) => new()
    {
        BookingId = row.BookingId,
        BookingCode = BookingCodes.Format(row.BookingCode),
        PropertyId = row.PropertyId,
        PropertyName = row.PropertyName,
        Source = row.Source.ToString(),
        CheckIn = row.CheckIn,
        CheckOut = row.CheckOut,
        Currency = row.Currency,
        Total = row.Total,
        TotalCents = row.Total is { } total ? PaymentCashRules.ToCents(total) : null,
        Collected = row.Collected,
        CollectedCents = row.Collected is { } collected ? PaymentCashRules.ToCents(collected) : null,
        ToCollect = row.ToCollect,
        ToCollectCents = PaymentCashRules.ToCents(row.ToCollect),
        Overdue = row.Overdue,
        Basis = row.Basis.ToString(),
    };
}

/// <summary>What is to collect in one currency.</summary>
public sealed class BalanceTotalDto
{
    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    /// <summary>The stays that make it.</summary>
    public int StayCount { get; init; }

    public static BalanceTotalDto From(HostBalanceTotal total) => new()
    {
        Currency = total.Currency,
        Amount = total.Amount,
        AmountCents = PaymentCashRules.ToCents(total.Amount),
        StayCount = total.StayCount,
    };
}
