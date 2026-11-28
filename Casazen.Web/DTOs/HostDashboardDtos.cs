using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class HostDashboardKpisDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    public DateOnly Today { get; init; }

    public int PropertyCount { get; init; }

    public HostDashboardOccupancyDto Occupancy { get; init; } = new();

    public HostDashboardRevenueDto Revenue { get; init; } = new();

    public HostDashboardStayListDto ArrivalsToday { get; init; } = new();

    public HostDashboardStayListDto DeparturesToday { get; init; } = new();

    public HostDashboardStayListDto UpcomingCheckIns { get; init; } = new();

    public IReadOnlyList<HostDashboardStayDto> RecentBookings { get; init; } = [];

    public HostDashboardCollectedDto? Collected { get; init; }

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

public sealed class HostDashboardPeriodDto
{
    public HostDashboardPeriodKind Kind { get; init; }

    public DateOnly From { get; init; }

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

public sealed class HostDashboardOccupancyDto
{
    public int OccupiedNights { get; init; }

    public int AvailableNights { get; init; }

    public int ClosedNights { get; init; }

    public decimal? Rate { get; init; }

    public static HostDashboardOccupancyDto From(HostDashboardOccupancy occupancy) => new()
    {
        OccupiedNights = occupancy.OccupiedNights,
        AvailableNights = occupancy.AvailableNights,
        ClosedNights = occupancy.ClosedNights,
        Rate = occupancy.Rate,
    };
}

public sealed class HostDashboardRevenueDto
{
    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public string Currency { get; init; } = "EUR";

    public int StayCount { get; init; }

    public static HostDashboardRevenueDto From(decimal amount, int stayCount) => new()
    {
        Amount = amount,
        AmountCents = PaymentCashRules.ToCents(amount),
        StayCount = stayCount,
    };
}

public sealed class HostDashboardCollectedDto
{
    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public string Currency { get; init; } = "EUR";

    public int PaymentCount { get; init; }

    public static HostDashboardCollectedDto From(HostDashboardCollected collected) => new()
    {
        Amount = collected.Amount,
        AmountCents = PaymentCashRules.ToCents(collected.Amount),
        PaymentCount = collected.PaymentCount,
    };
}

public sealed class HostDashboardDirectShareDto
{
    public int DirectStays { get; init; }

    public int Stays { get; init; }

    public decimal? Rate { get; init; }

    public static HostDashboardDirectShareDto From(HostDashboardDirectShare share) => new()
    {
        DirectStays = share.DirectStays,
        Stays = share.Stays,
        Rate = share.Rate,
    };
}

public sealed class HostDashboardPreviousDto
{
    public HostDashboardPeriodDto Period { get; init; } = new();

    public HostDashboardOccupancyDto Occupancy { get; init; } = new();

    public HostDashboardRevenueDto Revenue { get; init; } = new();

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

public sealed class HostDashboardIcalFeedDto
{
    public Guid FeedId { get; init; }

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string Channel { get; init; } = string.Empty;

    public string? Label { get; init; }

    public DateTime? LastImportAt { get; init; }

    public string? LastImportStatus { get; init; }

    public string? LastErrorCode { get; init; }

    public string? LastError { get; init; }

    public bool Deprecated { get; init; }
}
