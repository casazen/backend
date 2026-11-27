using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class CollectionsReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "cash";

    public ReportPeriodDto Period { get; init; } = new();

    public CollectionsTotalDto Total { get; init; } = new();

    public IReadOnlyList<CollectionsBucketDto> ByDate { get; init; } = [];

    public IReadOnlyList<CollectionsMethodRowDto> ByMethod { get; init; } = [];

    public IReadOnlyList<CollectionsChannelRowDto> ByChannel { get; init; } = [];

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

public sealed class CollectionsTotalDto
{
    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }
}

public sealed class CollectionsBucketDto
{
    public string Unit { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public DateOnly From { get; init; }

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

public sealed class CollectionsMethodRowDto
{
    public string Method { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }

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

public sealed class CollectionsChannelRowDto
{
    /// <summary>The channel, the name of the booking source: <c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c>, ... A key, never text to show.</summary>
    public string Source { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }

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

public sealed class StripeCreditsDto
{
    public bool Available { get; init; }

    public IReadOnlyList<StripeCreditRowDto> Payouts { get; init; } = [];

    public int CardPaymentCount { get; init; }

    public int PaymentsWithoutFigures { get; init; }

    public static StripeCreditsDto From(HostStripeCredits credits) => new()
    {
        Available = credits.Available,
        Payouts = credits.Payouts.Select(StripeCreditRowDto.From).ToList(),
        CardPaymentCount = credits.CardPaymentCount,
        PaymentsWithoutFigures = credits.PaymentsWithoutFigures,
    };
}

public sealed class StripeCreditRowDto
{
    public DateOnly ArrivalDate { get; init; }

    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int PaymentCount { get; init; }

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

public sealed class ChannelCreditsDto
{
    public bool Available { get; init; }

    public string ReportedBy { get; init; } = "channelManager";

    public IReadOnlyList<ChannelCreditRowDto> Rows { get; init; } = [];

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
    public string Source { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

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

public sealed class BalancesToCollectReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "balances";

    public DateOnly AsOf { get; init; }

    public DateOnly? Since { get; init; }

    public IReadOnlyList<BalanceRowDto> Rows { get; init; } = [];

    public IReadOnlyList<BalanceTotalDto> Totals { get; init; } = [];

    public static BalancesToCollectReportDto From(HostBalancesToCollect report, string title) => new()
    {
        Title = title,
        AsOf = report.AsOf,
        Since = report.Since,
        Rows = report.Rows.Select(BalanceRowDto.From).ToList(),
        Totals = report.Totals.Select(BalanceTotalDto.From).ToList(),
    };
}

public sealed class BalanceRowDto
{
    public Guid BookingId { get; init; }

    public string BookingCode { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateOnly CheckIn { get; init; }

    public DateOnly CheckOut { get; init; }

    public string Currency { get; init; } = string.Empty;

    public decimal? Total { get; init; }

    public long? TotalCents { get; init; }

    /// <summary>What came in; null for a channel reservation Octorate did not report the paid amount of (not available, never derived).</summary>
    public decimal? Collected { get; init; }

    public long? CollectedCents { get; init; }

    public decimal ToCollect { get; init; }

    public long ToCollectCents { get; init; }

    public bool Overdue { get; init; }

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

public sealed class BalanceTotalDto
{
    public string Currency { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int StayCount { get; init; }

    public static BalanceTotalDto From(HostBalanceTotal total) => new()
    {
        Currency = total.Currency,
        Amount = total.Amount,
        AmountCents = PaymentCashRules.ToCents(total.Amount),
        StayCount = total.StayCount,
    };
}
