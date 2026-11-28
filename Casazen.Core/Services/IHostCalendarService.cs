using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The calendar of all the host's properties in one read (SR-02a, <c>GET /api/calendar</c>): the stays, the portal reservations that
/// arrive as blocks, the host's own blocks and the interventions of every property the caller reaches, and the nights taken twice
/// that the Home puts among the things to do. Limited to the caller's <see cref="HostScope"/> in SQL, like every list of the host.
/// <c>GET /api/bookings/calendar</c>, the calendar of one property, is not this and does not change.
/// </summary>
/// <remarks>
/// It adds no rule of its own about occupancy: a booking takes its dates as <see cref="CheckoutHolds.OccupiesDates"/> says (so an
/// expired checkout hold is not on the calendar, a "pay at the property" request is), a block as <see cref="PropertyOccupancy"/>
/// does (one converted into a stay that stands for it is the stay). Days are Europe/Rome calendar dates. Every read is one query
/// for all the properties, never one per property.
/// </remarks>
public interface IHostCalendarService
{
    Task<HostCalendar> GetCalendarAsync(HostScope scope, HostCalendarQuery query, CancellationToken cancellationToken = default);

    Task<HostCalendarAlerts> GetAlertsAsync(HostScope scope, HostCalendarAlertsQuery query, CancellationToken cancellationToken = default);
}

public static class HostCalendarLimits
{
    public const int MaxWindowDays = 92;

    public const int MaxProperties = 50;

    public const int TodoAlertDays = 30;
}

public static class HostCalendarErrorCodes
{
    public const string WindowTooLarge = "calendar_window_too_large";

    public const string TooManyProperties = "calendar_too_many_properties";

    public const string WindowTooLargeMessageKey = "CalendarWindowTooLarge";
    public const string TooManyPropertiesMessageKey = "CalendarTooManyProperties";
}

public sealed record HostCalendarQuery(DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? PropertyIds = null);

public sealed record HostCalendarAlertsQuery(DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? PropertyIds = null);

public sealed record HostCalendar(
    DateOnly Today,
    int PropertiesTotal,
    IReadOnlyList<CalendarProperty> Properties,
    IReadOnlyList<CalendarItem> Items);

public enum CalendarPropertyStatus
{
    Active,

    Paused,
}

public sealed record CalendarProperty(
    Guid Id,
    string Name,
    string City,
    RentalMode RentalMode,
    CalendarPropertyStatus Status,
    int? MinNights,
    long BaseRateCents,
    decimal WeekendSurchargePercent,
    long CleaningFeeCents);

public enum CalendarItemType
{
    Booking,

    Ical,

    Block,

    Service,
}

public abstract record CalendarItem(Guid Id, Guid PropertyId, DateOnly From, DateOnly To)
{
    public abstract CalendarItemType Type { get; }
}

/// <summary>A stay of CasaZen.</summary>
/// <param name="Channel">Where it came from, as the list of bookings reads it (<see cref="BookingListRules.ChannelOf"/>).</param>
/// <param name="OnSiteRequestState">Where a "pay at the property" request stands; null for any other booking.</param>
/// <param name="RequestExpiresAt">When a request not yet answered is cancelled (UTC); null for any other booking.</param>
/// <param name="PaymentState">How much of the total came in (<see cref="BookingListRules.PaymentStateOf"/>).</param>
/// <param name="TotalCents">The total of the stay, in cents of euro.</param>
/// <param name="IcalFeedId">The calendar it was made from when the host turned a portal's block into this stay.</param>
/// <param name="ChannelLabel">The name of that calendar.</param>
/// <param name="OtaReviewReason">Why the portal's calendar asks to check the reservation again; null when nothing is to check.</param>
public sealed record BookingCalendarItem(
    Guid Id,
    Guid PropertyId,
    DateOnly From,
    DateOnly To,
    BookingStatus Status,
    string GuestName,
    int NumberOfGuests,
    BookingChannel Channel,
    PaymentOption PaymentOption,
    OnSiteRequestState? OnSiteRequestState,
    DateTime? RequestExpiresAt,
    BookingPaymentState PaymentState,
    long TotalCents,
    Guid? IcalFeedId,
    string? ChannelLabel,
    OtaStayReviewReason? OtaReviewReason) : CalendarItem(Id, PropertyId, From, To)
{
    public override CalendarItemType Type => CalendarItemType.Booking;
}

/// <summary>A reservation of a portal as it arrives, a block of an iCal calendar.</summary>
/// <param name="Channel">Airbnb, Booking.com or any other portal.</param>
/// <param name="FeedLabel">The name the host gave to the calendar.</param>
/// <param name="Convertible">The host can turn it into a stay with the guest's name.</param>
/// <param name="BookingId">The stay the host turned it into, when it is not the one that stands for it (its dates changed on the portal).</param>
/// <param name="Note">What the portal wrote on the event; for the host only.</param>
public sealed record IcalCalendarItem(
    Guid Id,
    Guid PropertyId,
    DateOnly From,
    DateOnly To,
    BookingChannel Channel,
    string? FeedLabel,
    bool Convertible,
    Guid? BookingId,
    string? Note) : CalendarItem(Id, PropertyId, From, To)
{
    public override CalendarItemType Type => CalendarItemType.Ical;
}

public sealed record BlockCalendarItem(
    Guid Id,
    Guid PropertyId,
    DateOnly From,
    DateOnly To,
    CalendarBlockReason Reason,
    string? Note) : CalendarItem(Id, PropertyId, From, To)
{
    public override CalendarItemType Type => CalendarItemType.Block;
}

public enum CalendarServiceKind
{
    Cleaning,

    Maintenance,

    Other,
}

public static class CalendarServiceKinds
{
    public static CalendarServiceKind Of(string category) => category switch
    {
        ServiceCategories.Cleaning or ServiceCategories.Laundry => CalendarServiceKind.Cleaning,
        ServiceCategories.Maintenance or ServiceCategories.Plumbing or ServiceCategories.Electrical => CalendarServiceKind.Maintenance,
        _ => CalendarServiceKind.Other,
    };
}

/// <summary>An intervention of a supplier: placed on the day of the work, or, when it has no time, on the day its stay leaves.</summary>
/// <param name="Category">The category code (<see cref="ServiceCategories"/>): the client translates it.</param>
/// <param name="ServiceName">The service of the supplier's catalog it was asked as; null for a request by category only.</param>
/// <param name="BookingId">The stay the request is for.</param>
/// <param name="StartAt">When the work starts (UTC); null for a request with no time.</param>
/// <param name="EndAt">When the work ends (UTC); null for a request with no time.</param>
public sealed record ServiceCalendarItem(
    Guid Id,
    Guid PropertyId,
    DateOnly From,
    DateOnly To,
    CalendarServiceKind Kind,
    string Category,
    string? ServiceName,
    ServiceRequestStatus Status,
    Guid SupplierOrgId,
    string SupplierName,
    Guid? BookingId,
    DateTime? StartAt,
    DateTime? EndAt) : CalendarItem(Id, PropertyId, From, To)
{
    public override CalendarItemType Type => CalendarItemType.Service;
}

public sealed record HostCalendarAlerts(DateOnly Today, DateOnly From, DateOnly To, IReadOnlyList<CalendarAlert> Alerts);
