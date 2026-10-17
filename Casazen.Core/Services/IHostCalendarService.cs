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
    /// <summary>The calendar of <paramref name="query"/>'s days (both ends included): the properties and the items that have a day in the range.</summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="HostCalendarErrorCodes.WindowTooLarge"/> or <see cref="HostCalendarErrorCodes.TooManyProperties"/> (422).
    /// </exception>
    Task<HostCalendar> GetCalendarAsync(HostScope scope, HostCalendarQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the calendar of <paramref name="query"/>'s days (both ends included) asks the host to look at, the most serious first
    /// (<see cref="CalendarAlertRules"/>): the one calculation the Home puts among its things to do. Only nights taken twice so far.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="HostCalendarErrorCodes.WindowTooLarge"/> or <see cref="HostCalendarErrorCodes.TooManyProperties"/> (422).
    /// </exception>
    Task<HostCalendarAlerts> GetAlertsAsync(HostScope scope, HostCalendarAlertsQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The limits of the calendar reads.</summary>
public static class HostCalendarLimits
{
    /// <summary>The longest range of days a read takes, ends included.</summary>
    public const int MaxWindowDays = 92;

    /// <summary>The most properties a read is asked for by id; the calendar lists at most this many when it is asked for none.</summary>
    public const int MaxProperties = 50;

    /// <summary>The days the things to do of the Home look at: today and the 29 after it.</summary>
    public const int TodoAlertDays = 30;
}

/// <summary>Stable <c>code</c> values of the calendar errors: part of the API contract, never rename one.</summary>
public static class HostCalendarErrorCodes
{
    /// <summary>422: the range asked is longer than <see cref="HostCalendarLimits.MaxWindowDays"/> days.</summary>
    public const string WindowTooLarge = "calendar_window_too_large";

    /// <summary>422: more than <see cref="HostCalendarLimits.MaxProperties"/> properties asked for by id.</summary>
    public const string TooManyProperties = "calendar_too_many_properties";

    public const string WindowTooLargeMessageKey = "CalendarWindowTooLarge";
    public const string TooManyPropertiesMessageKey = "CalendarTooManyProperties";
}

/// <param name="From">First day of the range.</param>
/// <param name="To">Last day of the range, included.</param>
/// <param name="PropertyIds">
/// Only these properties of the caller's reach (the ones it does not reach are left out, as if they did not exist); null or empty =
/// all of them.
/// </param>
public sealed record HostCalendarQuery(DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? PropertyIds = null);

/// <param name="From">First day of the range.</param>
/// <param name="To">Last day of the range, included.</param>
/// <param name="PropertyIds">Only these properties of the caller's reach; null or empty = all of them.</param>
public sealed record HostCalendarAlertsQuery(DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? PropertyIds = null);

/// <summary>The calendar of the host's properties for a range of days.</summary>
/// <param name="Today">Today in Europe/Rome.</param>
/// <param name="PropertiesTotal">How many short-rent properties the caller reaches; more than <see cref="Properties"/> when the list was cut to <see cref="HostCalendarLimits.MaxProperties"/>.</param>
/// <param name="Properties">The properties, by name.</param>
/// <param name="Items">The items that have a day in the range, by first day.</param>
public sealed record HostCalendar(
    DateOnly Today,
    int PropertiesTotal,
    IReadOnlyList<CalendarProperty> Properties,
    IReadOnlyList<CalendarItem> Items);

public enum CalendarPropertyStatus
{
    /// <summary>Takes bookings.</summary>
    Active,

    /// <summary>The host paused it (<see cref="Property.IsPaused"/>): hidden from the booking site, still on the host's calendar.</summary>
    Paused,
}

/// <summary>A property of the calendar, with what a screen needs to place its row.</summary>
/// <param name="MinNights">Fewest nights the booking site accepts; null = no minimum (<see cref="Property.MinNights"/>).</param>
/// <param name="BaseRateCents">The nightly rate, in cents of euro.</param>
/// <param name="WeekendSurchargePercent">Percent added to the rate on a Friday or Saturday night (<see cref="Property.WeekendSurchargePercent"/>).</param>
/// <param name="CleaningFeeCents">The cleaning fee of a stay, in cents of euro.</param>
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
    /// <summary>A stay of CasaZen: the booking site, entered by the host, or a portal's reservation the host turned into a stay.</summary>
    Booking,

    /// <summary>A reservation of a portal as it arrives, a block of an iCal calendar: dates only.</summary>
    Ical,

    /// <summary>The host's own block.</summary>
    Block,

    /// <summary>An intervention (a service request) of a supplier.</summary>
    Service,
}

/// <summary>
/// One thing on the calendar. <see cref="From"/> and <see cref="To"/> are calendar days: the first night and the day of the departure
/// (the first day the item no longer takes a night, so a stay that leaves on the 12th and a stay that arrives on the 12th do not
/// overlap); an intervention has the day of the work and the day after.
/// </summary>
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

/// <summary>The host's own block: the dates are closed.</summary>
/// <param name="Reason">Why the host closed them; <see cref="CalendarBlockReason.ModeChange"/> is CasaZen's.</param>
/// <param name="Note">What the host wrote.</param>
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
    /// <summary>Cleaning, laundry.</summary>
    Cleaning,

    /// <summary>Maintenance, plumbing, electrical.</summary>
    Maintenance,

    /// <summary>Any other category.</summary>
    Other,
}

/// <summary>What kind of work a service category is, for the calendar.</summary>
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

/// <summary>The alerts of the calendar for a range of days.</summary>
/// <param name="Alerts">The most serious first, then by day (<see cref="CalendarAlertRules.Sort"/>).</param>
public sealed record HostCalendarAlerts(DateOnly Today, DateOnly From, DateOnly To, IReadOnlyList<CalendarAlert> Alerts);
