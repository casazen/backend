using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// KPIs of the host dashboard for a period (PC-16, A2-29): definitions in <see cref="IHostDashboardService"/> and
/// <c>docs/TECHNICAL.md</c> ("Host dashboard"). Dates are Europe/Rome calendar dates (<c>yyyy-MM-dd</c>).
/// </summary>
public sealed class HostDashboardKpisDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    /// <summary>Today in Europe/Rome.</summary>
    public DateOnly Today { get; init; }

    /// <summary>Active properties of the caller's scope (the denominator of the occupancy).</summary>
    public int PropertyCount { get; init; }

    public HostDashboardOccupancyDto Occupancy { get; init; } = new();

    public HostDashboardRevenueDto Revenue { get; init; } = new();

    /// <summary>Confirmed stays arriving today (count and the first items).</summary>
    public HostDashboardStayListDto ArrivalsToday { get; init; } = new();

    /// <summary>Confirmed stays leaving today (count and the first items).</summary>
    public HostDashboardStayListDto DeparturesToday { get; init; } = new();

    /// <summary>Confirmed check-ins from today on, soonest first; never a cancelled booking.</summary>
    public HostDashboardStayListDto UpcomingCheckIns { get; init; } = new();

    /// <summary>The last bookings created, any status.</summary>
    public IReadOnlyList<HostDashboardStayDto> RecentBookings { get; init; } = [];

    /// <summary>
    /// Money collected in the period, by cash (SR-03): the day it came in, not the days of the stay it pays. <c>null</c> for a
    /// caller who may not read payments.
    /// </summary>
    public HostDashboardCollectedDto? Collected { get; init; }

    /// <summary>Share of the confirmed stays of the period that came from the booking site (SR-03).</summary>
    public HostDashboardDirectShareDto DirectShare { get; init; } = new();

    /// <summary>
    /// The figures of the period of the same number of nights that ends where this one starts (SR-03); <c>null</c> unless the
    /// request asked for <c>compare=true</c>.
    /// </summary>
    public HostDashboardPreviousDto? Previous { get; init; }

    public static HostDashboardKpisDto From(HostDashboardKpis kpis) => new()
    {
        Period = HostDashboardPeriodDto.Of(kpis.Period),
        Today = DateOnly.FromDateTime(kpis.TodayInRome),
        PropertyCount = kpis.PropertyCount,
        Occupancy = HostDashboardOccupancyDto.From(kpis.Occupancy),
        Revenue = HostDashboardRevenueDto.From(kpis.Revenue, kpis.RevenueStayCount),
        ArrivalsToday = HostDashboardStayListDto.From(kpis.ArrivalsToday),
        DeparturesToday = HostDashboardStayListDto.From(kpis.DeparturesToday),
        UpcomingCheckIns = HostDashboardStayListDto.From(kpis.UpcomingCheckIns),
        RecentBookings = kpis.RecentBookings.Select(HostDashboardStayDto.From).ToList(),
        Collected = kpis.Collected is null ? null : HostDashboardCollectedDto.From(kpis.Collected),
        DirectShare = HostDashboardDirectShareDto.From(kpis.DirectShare),
        Previous = kpis.Previous is null ? null : HostDashboardPreviousDto.From(kpis.Previous),
    };
}

/// <summary>The nights from <see cref="From"/> to <see cref="To"/>, both included.</summary>
public sealed class HostDashboardPeriodDto
{
    /// <summary><c>Month</c>, <c>Last30Days</c> or <c>Next30Days</c>.</summary>
    public HostDashboardPeriodKind Kind { get; init; }

    /// <summary>First night of the period.</summary>
    public DateOnly From { get; init; }

    /// <summary>Last night of the period (included).</summary>
    public DateOnly To { get; init; }

    public int Nights { get; init; }

    public static HostDashboardPeriodDto Of(HostDashboardPeriod period) => new()
    {
        Kind = period.Kind,
        From = DateOnly.FromDateTime(period.From),
        To = DateOnly.FromDateTime(period.To.AddDays(-1)),
        Nights = period.Nights,
    };
}

/// <summary>Occupied / available nights of the period, over the active properties.</summary>
public sealed class HostDashboardOccupancyDto
{
    /// <summary>Property-nights taken by a booking or by a block imported from an iCal feed.</summary>
    public int OccupiedNights { get; init; }

    /// <summary>Property-nights of the period minus <see cref="ClosedNights"/>.</summary>
    public int AvailableNights { get; init; }

    /// <summary>Property-nights closed by a manual block only: out of the availability.</summary>
    public int ClosedNights { get; init; }

    /// <summary><see cref="OccupiedNights"/> / <see cref="AvailableNights"/>, from 0 to 1; null when nothing is available.</summary>
    public decimal? Rate { get; init; }

    public static HostDashboardOccupancyDto From(HostDashboardOccupancy occupancy) => new()
    {
        OccupiedNights = occupancy.OccupiedNights,
        AvailableNights = occupancy.AvailableNights,
        ClosedNights = occupancy.ClosedNights,
        Rate = occupancy.Rate,
    };
}

/// <summary>Revenue of the period: confirmed stays pro rata per night, base price (lodging + cleaning, no tourist tax).</summary>
public sealed class HostDashboardRevenueDto
{
    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro (SR-03): the figure for new screens, exact.</summary>
    public long AmountCents { get; init; }

    /// <summary>Always EUR: properties have no currency.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>Confirmed stays with at least one night in the period.</summary>
    public int StayCount { get; init; }

    public static HostDashboardRevenueDto From(decimal amount, int stayCount) => new()
    {
        Amount = amount,
        AmountCents = PaymentCashRules.ToCents(amount),
        StayCount = stayCount,
    };
}

/// <summary>
/// Money collected in the period, by cash (SR-03): the payments settled on those days, net of what was refunded
/// (<see cref="PaymentCashRules"/>). Next to <see cref="HostDashboardRevenueDto"/>, which counts the nights of the stays.
/// </summary>
public sealed class HostDashboardCollectedDto
{
    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    public string Currency { get; init; } = "EUR";

    /// <summary>Payments settled in the period.</summary>
    public int PaymentCount { get; init; }

    public static HostDashboardCollectedDto From(HostDashboardCollected collected) => new()
    {
        Amount = collected.Amount,
        AmountCents = PaymentCashRules.ToCents(collected.Amount),
        PaymentCount = collected.PaymentCount,
    };
}

/// <summary>Of the confirmed stays of the period (the ones of the revenue), how many came from the booking site (SR-03).</summary>
public sealed class HostDashboardDirectShareDto
{
    /// <summary>Confirmed stays with source <c>Direct</c> (the public checkout, not the ones the host entered).</summary>
    public int DirectStays { get; init; }

    /// <summary>All the confirmed stays of the period.</summary>
    public int Stays { get; init; }

    /// <summary><see cref="DirectStays"/> / <see cref="Stays"/>, from 0 to 1; null without any stay.</summary>
    public decimal? Rate { get; init; }

    public static HostDashboardDirectShareDto From(HostDashboardDirectShare share) => new()
    {
        DirectStays = share.DirectStays,
        Stays = share.Stays,
        Rate = share.Rate,
    };
}

/// <summary>The figures of the period before the one asked for, to compare with (SR-03): the same definitions, other days.</summary>
public sealed class HostDashboardPreviousDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    public HostDashboardOccupancyDto Occupancy { get; init; } = new();

    public HostDashboardRevenueDto Revenue { get; init; } = new();

    /// <summary><c>null</c> for a caller who may not read payments.</summary>
    public HostDashboardCollectedDto? Collected { get; init; }

    public HostDashboardDirectShareDto DirectShare { get; init; } = new();

    public static HostDashboardPreviousDto From(HostDashboardFigures figures) => new()
    {
        Period = HostDashboardPeriodDto.Of(figures.Period),
        Occupancy = HostDashboardOccupancyDto.From(figures.Occupancy),
        Revenue = HostDashboardRevenueDto.From(figures.Revenue, figures.RevenueStayCount),
        Collected = figures.Collected is null ? null : HostDashboardCollectedDto.From(figures.Collected),
        DirectShare = HostDashboardDirectShareDto.From(figures.DirectShare),
    };
}

public sealed class HostDashboardStayListDto
{
    /// <summary>All the stays of the list; <see cref="Items"/> carries only the first ones.</summary>
    public int Count { get; init; }

    public IReadOnlyList<HostDashboardStayDto> Items { get; init; } = [];

    public static HostDashboardStayListDto From(HostDashboardStayList list) => new()
    {
        Count = list.Count,
        Items = list.Items.Select(HostDashboardStayDto.From).ToList(),
    };
}

public sealed class HostDashboardStayDto
{
    public Guid BookingId { get; init; }

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string GuestName { get; init; } = string.Empty;

    public DateOnly CheckInDate { get; init; }

    public DateOnly CheckOutDate { get; init; }

    public string Status { get; init; } = string.Empty;

    public decimal TotalPrice { get; init; }

    public DateTime CreatedAt { get; init; }

    public static HostDashboardStayDto From(HostDashboardStay stay) => new()
    {
        BookingId = stay.BookingId,
        PropertyId = stay.PropertyId,
        PropertyName = stay.PropertyName,
        GuestName = stay.GuestName,
        CheckInDate = DateOnly.FromDateTime(stay.CheckInDate),
        CheckOutDate = DateOnly.FromDateTime(stay.CheckOutDate),
        Status = stay.Status.ToString(),
        TotalPrice = stay.TotalPrice,
        CreatedAt = stay.CreatedAt,
    };
}

/// <summary>
/// State of one iCal import feed for the dashboard widget (PC-16): same error contract as
/// <see cref="PropertyIcalFeedDto"/> (stable code and localized message), never the URL.
/// </summary>
public sealed class HostDashboardIcalFeedDto
{
    public Guid FeedId { get; init; }

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    /// <summary>Airbnb, BookingCom or Other.</summary>
    public string Channel { get; init; } = string.Empty;

    public string? Label { get; init; }

    public DateTime? LastImportAt { get; init; }

    /// <summary>Success, PartialFailure, Failure, Syncing; null when never synced.</summary>
    public string? LastImportStatus { get; init; }

    /// <summary>Stable code of the last sync error (<c>ical_unreachable</c>, ...).</summary>
    public string? LastErrorCode { get; init; }

    /// <summary>Localized message of <see cref="LastErrorCode"/>, never an exception message.</summary>
    public string? LastError { get; init; }
}
