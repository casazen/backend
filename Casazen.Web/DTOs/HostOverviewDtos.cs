using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The numbers of the Home for a period, compared with the period before (SR-03b, <c>GET /api/dashboard/overview</c>): definitions in
/// <see cref="IHostDashboardService"/> and <c>docs/TECHNICAL.md</c> ("Host dashboard"). Dates are Europe/Rome calendar dates
/// (<c>yyyy-MM-dd</c>); the figures are those of <c>kpis</c>, computed by the same code. Money is payment data (SR-D7): for a caller
/// without <c>payment.read</c> <see cref="Revenue"/>, <see cref="Collected"/>, <see cref="Series"/> and the same inside
/// <see cref="Previous"/> are not in the answer at all.
/// </summary>
public sealed class HostOverviewDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    /// <summary>Today in Europe/Rome.</summary>
    public DateOnly Today { get; init; }

    /// <summary>Active short-rent properties of the caller's scope (the denominator of the occupancy).</summary>
    public int PropertyCount { get; init; }

    public HostOverviewOccupancyDto Occupancy { get; init; } = new();

    /// <summary>Revenue of the period by accrual, with the difference from the period before. Absent without <c>payment.read</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewRevenueDto? Revenue { get; init; }

    /// <summary>Money that came in during the period, by cash. Absent without <c>payment.read</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewCollectedDto? Collected { get; init; }

    /// <summary>Share of the confirmed stays of the period that came from the booking site.</summary>
    public HostDashboardDirectShareDto DirectShare { get; init; } = new();

    /// <summary>The stays that arrive in the period and in the one before, and the requests waiting for the host.</summary>
    public HostOverviewUpcomingDto Upcoming { get; init; } = new();

    /// <summary>The next three arrivals from today, whatever the period, soonest first (a request waiting for the host included).</summary>
    public IReadOnlyList<HostOverviewArrivalDto> NextArrivals { get; init; } = [];

    /// <summary>The figures of the period of the same number of nights that ends where this one starts.</summary>
    public HostOverviewPreviousDto Previous { get; init; } = new();

    /// <summary>Revenue by week or month, adding up to <see cref="Revenue"/>. Absent without <c>payment.read</c>.</summary>
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

/// <summary>Occupied / available nights of a period, over the active properties, and the difference from the period before.</summary>
public sealed class HostOverviewOccupancyDto
{
    /// <summary>Property-nights taken by a booking or by a block imported from an iCal feed.</summary>
    public int OccupiedNights { get; init; }

    /// <summary>Property-nights of the period minus <see cref="ClosedNights"/>.</summary>
    public int AvailableNights { get; init; }

    /// <summary>Property-nights closed by a manual block only: out of the availability.</summary>
    public int ClosedNights { get; init; }

    /// <summary><see cref="OccupiedNights"/> / <see cref="AvailableNights"/>, from 0 to 1; null when nothing is available.</summary>
    public decimal? Rate { get; init; }

    /// <summary>
    /// The rate minus the one of the period before, in percentage points to one decimal; <c>null</c> in <c>previous</c> and when either
    /// period has nothing available.
    /// </summary>
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

/// <summary>
/// Revenue of a period by accrual: the confirmed stays pro rata per night, base price (lodging + cleaning, no tourist tax). Not the
/// money that came in, which is <see cref="HostOverviewCollectedDto"/>: <see cref="Basis"/> says which one a number is.
/// </summary>
public sealed class HostOverviewRevenueDto
{
    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    /// <summary>Always EUR: properties have no currency.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>Confirmed stays with at least one night in the period.</summary>
    public int StayCount { get; init; }

    /// <summary>Always <c>accrual</c>: the nights of the stays, whenever the money came in.</summary>
    public string Basis { get; init; } = "accrual";

    /// <summary>
    /// How much more (positive) or less (negative) than the period before, in percent to one decimal; <c>null</c> in <c>previous</c>
    /// and when the period before had no revenue.
    /// </summary>
    public decimal? DeltaPercent { get; init; }

    public static HostOverviewRevenueDto From(HostOverviewRevenue revenue) => new()
    {
        Amount = revenue.Amount,
        AmountCents = PaymentCashRules.ToCents(revenue.Amount),
        StayCount = revenue.StayCount,
        DeltaPercent = revenue.DeltaPercent,
    };
}

/// <summary>Money that came in during a period, by cash (<see cref="PaymentCashRules"/>): the day it came in, not the days of the stay it pays.</summary>
public sealed class HostOverviewCollectedDto
{
    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    public string Currency { get; init; } = "EUR";

    /// <summary>Payments settled in the period.</summary>
    public int PaymentCount { get; init; }

    /// <summary>Always <c>cash</c>.</summary>
    public string Basis { get; init; } = "cash";

    public static HostOverviewCollectedDto From(HostDashboardCollected collected) => new()
    {
        Amount = collected.Amount,
        AmountCents = PaymentCashRules.ToCents(collected.Amount),
        PaymentCount = collected.PaymentCount,
    };
}

/// <summary>The stays that arrive in the period (a request still waiting for the host included) and in the one before.</summary>
public sealed class HostOverviewUpcomingDto
{
    /// <summary>Confirmed stays that arrive in the period, and the requests waiting for the host.</summary>
    public int Count { get; init; }

    /// <summary>The same count over the period before.</summary>
    public int PreviousCount { get; init; }

    /// <summary>Of <see cref="Count"/>, the "pay at the property" requests the host has still to accept or decline.</summary>
    public int PendingApproval { get; init; }

    public static HostOverviewUpcomingDto From(HostOverviewUpcoming upcoming) => new()
    {
        Count = upcoming.Count,
        PreviousCount = upcoming.PreviousCount,
        PendingApproval = upcoming.PendingApproval,
    };
}

/// <summary>An arrival to come. No money: the Home lists who comes, the amounts are on the booking.</summary>
public sealed class HostOverviewArrivalDto
{
    public Guid BookingId { get; init; }

    /// <summary>The code the guest knows (<c>XXXXX-XXXXX</c>).</summary>
    public string BookingCode { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string GuestName { get; init; } = string.Empty;

    public int NumberOfGuests { get; init; }

    public DateOnly CheckInDate { get; init; }

    public DateOnly CheckOutDate { get; init; }

    /// <summary><c>Confirmed</c> or <c>Pending</c> (a request waiting for the host).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Where the booking came from: <c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>, ...</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The arrival is a "pay at the property" request the host has still to accept or decline.</summary>
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

/// <summary>The figures of the period before the one asked for: the same definitions, other days, nothing compared again.</summary>
public sealed class HostOverviewPreviousDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    public HostOverviewOccupancyDto Occupancy { get; init; } = new();

    /// <summary>Absent without <c>payment.read</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostOverviewRevenueDto? Revenue { get; init; }

    /// <summary>Absent without <c>payment.read</c>.</summary>
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

/// <summary>
/// Revenue by week or month of the period, next to the same days of the period before. The amounts add up to the revenue of their
/// period to the cent. A bar that starts after today is a forecast of the stays already confirmed.
/// </summary>
public sealed class HostOverviewSeriesDto
{
    /// <summary><c>week</c> (ISO weeks from Monday, up to 62 days) or <c>month</c>.</summary>
    public string Unit { get; init; } = string.Empty;

    public IReadOnlyList<HostOverviewBucketDto> Buckets { get; init; } = [];

    public static HostOverviewSeriesDto From(HostOverviewSeries series) => new()
    {
        Unit = series.Unit == HostOverviewSeriesUnit.Week ? "week" : "month",
        Buckets = series.Buckets.Select(HostOverviewBucketDto.Of).ToList(),
    };
}

/// <summary>A bar of the chart.</summary>
public sealed class HostOverviewBucketDto
{
    /// <summary>A stable key, never text to show: the ISO week (<c>2026-W42</c>) or the month (<c>2026-10</c>). Show the dates in the user's language.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>First night of the bar.</summary>
    public DateOnly From { get; init; }

    /// <summary>Last night of the bar (included).</summary>
    public DateOnly To { get; init; }

    /// <summary>Euros, to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    /// <summary>Euros of the same days of the period before.</summary>
    public decimal PreviousAmount { get; init; }

    /// <summary><see cref="PreviousAmount"/> in cents of euro, exact.</summary>
    public long PreviousAmountCents { get; init; }

    /// <summary>The bar starts after today in Europe/Rome: what is there is a forecast ("prevista").</summary>
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
