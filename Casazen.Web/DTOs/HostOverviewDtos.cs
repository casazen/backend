using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class HostOverviewDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    public DateOnly Today { get; init; }

    public int PropertyCount { get; init; }

    public HostOverviewOccupancyDto Occupancy { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewRevenueDto? Revenue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewCollectedDto? Collected { get; init; }

    public HostDashboardDirectShareDto DirectShare { get; init; } = new();

    public HostOverviewUpcomingDto Upcoming { get; init; } = new();

    public IReadOnlyList<HostOverviewArrivalDto> NextArrivals { get; init; } = [];

    public HostOverviewPreviousDto Previous { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewSeriesDto? Series { get; init; }

    public static HostOverviewDto From(HostOverview overview) => new()
    {
        Period = HostDashboardPeriodDto.Of(overview.Period),
        Today = DateOnly.FromDateTime(overview.TodayInRome),
        PropertyCount = overview.PropertyCount,
        Occupancy = HostOverviewOccupancyDto.From(overview.Occupancy),
        Revenue = overview.Revenue is null ? null : HostOverviewRevenueDto.From(overview.Revenue),
        Collected = overview.Collected is null ? null : HostOverviewCollectedDto.From(overview.Collected),
        DirectShare = HostDashboardDirectShareDto.From(overview.DirectShare),
        Upcoming = HostOverviewUpcomingDto.From(overview.Upcoming),
        NextArrivals = overview.NextArrivals.Select(HostOverviewArrivalDto.From).ToList(),
        Previous = HostOverviewPreviousDto.From(overview.Previous),
        Series = overview.Series is null ? null : HostOverviewSeriesDto.From(overview.Series),
    };
}

public sealed class HostOverviewOccupancyDto
{
    public int OccupiedNights { get; init; }

    public int AvailableNights { get; init; }

    public int ClosedNights { get; init; }

    public decimal? Rate { get; init; }

    public decimal? DeltaPoints { get; init; }

    public static HostOverviewOccupancyDto From(HostOverviewOccupancy occupancy) => new()
    {
        OccupiedNights = occupancy.Occupancy.OccupiedNights,
        AvailableNights = occupancy.Occupancy.AvailableNights,
        ClosedNights = occupancy.Occupancy.ClosedNights,
        Rate = occupancy.Occupancy.Rate,
        DeltaPoints = occupancy.DeltaPoints,
    };
}

public sealed class HostOverviewRevenueDto
{
    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public string Currency { get; init; } = "EUR";

    public int StayCount { get; init; }

    public string Basis { get; init; } = "accrual";

    public decimal? DeltaPercent { get; init; }

    public static HostOverviewRevenueDto From(HostOverviewRevenue revenue) => new()
    {
        Amount = revenue.Amount,
        AmountCents = PaymentCashRules.ToCents(revenue.Amount),
        StayCount = revenue.StayCount,
        DeltaPercent = revenue.DeltaPercent,
    };
}

public sealed class HostOverviewCollectedDto
{
    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public string Currency { get; init; } = "EUR";

    public int PaymentCount { get; init; }

    public string Basis { get; init; } = "cash";

    public static HostOverviewCollectedDto From(HostDashboardCollected collected) => new()
    {
        Amount = collected.Amount,
        AmountCents = PaymentCashRules.ToCents(collected.Amount),
        PaymentCount = collected.PaymentCount,
    };
}

public sealed class HostOverviewUpcomingDto
{
    public int Count { get; init; }

    public int PreviousCount { get; init; }

    public int PendingApproval { get; init; }

    public static HostOverviewUpcomingDto From(HostOverviewUpcoming upcoming) => new()
    {
        Count = upcoming.Count,
        PreviousCount = upcoming.PreviousCount,
        PendingApproval = upcoming.PendingApproval,
    };
}

public sealed class HostOverviewArrivalDto
{
    public Guid BookingId { get; init; }

    public string BookingCode { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string GuestName { get; init; } = string.Empty;

    public int NumberOfGuests { get; init; }

    public DateOnly CheckInDate { get; init; }

    public DateOnly CheckOutDate { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public bool AwaitingApproval { get; init; }

    public static HostOverviewArrivalDto From(HostDashboardStay stay) => new()
    {
        BookingId = stay.BookingId,
        BookingCode = BookingCodes.Format(stay.BookingCode),
        PropertyId = stay.PropertyId,
        PropertyName = stay.PropertyName,
        GuestName = stay.GuestName,
        NumberOfGuests = stay.NumberOfGuests,
        CheckInDate = DateOnly.FromDateTime(stay.CheckInDate),
        CheckOutDate = DateOnly.FromDateTime(stay.CheckOutDate),
        Status = stay.Status.ToString(),
        Source = stay.Source.ToString(),
        AwaitingApproval = stay.Status == Casazen.Core.Entities.BookingStatus.Pending,
    };
}

public sealed class HostOverviewPreviousDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    public HostOverviewOccupancyDto Occupancy { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewRevenueDto? Revenue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewCollectedDto? Collected { get; init; }

    public HostDashboardDirectShareDto DirectShare { get; init; } = new();

    public static HostOverviewPreviousDto From(HostOverviewPrevious previous) => new()
    {
        Period = HostDashboardPeriodDto.Of(previous.Period),
        Occupancy = HostOverviewOccupancyDto.From(previous.Occupancy),
        Revenue = previous.Revenue is null ? null : HostOverviewRevenueDto.From(previous.Revenue),
        Collected = previous.Collected is null ? null : HostOverviewCollectedDto.From(previous.Collected),
        DirectShare = HostDashboardDirectShareDto.From(previous.DirectShare),
    };
}

public sealed class HostOverviewSeriesDto
{
    public string Unit { get; init; } = string.Empty;

    public IReadOnlyList<HostOverviewBucketDto> Buckets { get; init; } = [];

    public static HostOverviewSeriesDto From(HostOverviewSeries series) => new()
    {
        Unit = series.Unit == HostOverviewSeriesUnit.Week ? "week" : "month",
        Buckets = series.Buckets.Select(HostOverviewBucketDto.Of).ToList(),
    };
}

public sealed class HostOverviewBucketDto
{
    public string Label { get; init; } = string.Empty;

    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public decimal PreviousAmount { get; init; }

    public long PreviousAmountCents { get; init; }

    public bool IsFuture { get; init; }

    public static HostOverviewBucketDto Of(HostOverviewBucket bucket) => new()
    {
        Label = bucket.Label,
        From = DateOnly.FromDateTime(bucket.From),
        To = DateOnly.FromDateTime(bucket.To.AddDays(-1)),
        Amount = bucket.Amount,
        AmountCents = PaymentCashRules.ToCents(bucket.Amount),
        PreviousAmount = bucket.PreviousAmount,
        PreviousAmountCents = PaymentCashRules.ToCents(bucket.PreviousAmount),
        IsFuture = bucket.IsFuture,
    };
}
