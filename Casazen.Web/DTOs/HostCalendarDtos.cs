using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The calendar of all the host's properties (SR-02a, <c>GET /api/calendar</c>): definitions in <see cref="IHostCalendarService"/> and
/// <c>docs/TECHNICAL.md</c> ("Host calendar"). Days are calendar dates (<c>yyyy-MM-dd</c>, Europe/Rome), instants are UTC, money is
/// in cents of euro.
/// </summary>
public sealed class HostCalendarDto
{
    /// <summary>The zone asked for (the default is Europe/Rome); it changes only <see cref="UtcOffsetMinutes"/>, never a date.</summary>
    public string Timezone { get; init; } = string.Empty;

    /// <summary>The offset of <see cref="Timezone"/> from UTC now, in minutes.</summary>
    public int UtcOffsetMinutes { get; init; }

    /// <summary>Today in Europe/Rome.</summary>
    public DateOnly Today { get; init; }

    /// <summary>First day of the range asked for.</summary>
    public DateOnly From { get; init; }

    /// <summary>Last day of the range asked for, included.</summary>
    public DateOnly To { get; init; }

    /// <summary>How many short-rent properties the caller reaches: more than <see cref="Properties"/> when the list was cut to 50.</summary>
    public int PropertiesTotal { get; init; }

    /// <summary>The properties, by name (at most 50).</summary>
    public IReadOnlyList<HostCalendarPropertyDto> Properties { get; init; } = [];

    /// <summary>
    /// Everything that has a day in the range (arrival day to departure day, both included), by first day: stays, portal reservations,
    /// blocks and interventions of all the properties, told apart by <see cref="HostCalendarItemDto.Type"/>.
    /// </summary>
    public IReadOnlyList<HostCalendarItemDto> Items { get; init; } = [];

    public static HostCalendarDto Of(HostCalendar calendar, DateOnly from, DateOnly to, string timezone, int utcOffsetMinutes) => new()
    {
        Timezone = timezone,
        UtcOffsetMinutes = utcOffsetMinutes,
        Today = calendar.Today,
        From = from,
        To = to,
        PropertiesTotal = calendar.PropertiesTotal,
        Properties = calendar.Properties.Select(HostCalendarPropertyDto.From).ToList(),
        Items = calendar.Items.Select(HostCalendarItemDto.Of).ToList(),
    };
}

/// <summary>A property of the calendar: a row of the timeline.</summary>
public sealed class HostCalendarPropertyDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    /// <summary>Always <c>Short</c>: a property in long-term mode has no place on this calendar.</summary>
    public RentalMode RentalMode { get; init; }

    /// <summary><c>Active</c> or <c>Paused</c> (the host paused it: hidden from the booking site, still here).</summary>
    public CalendarPropertyStatus Status { get; init; }

    /// <summary>Fewest nights the booking site accepts; <c>null</c> = no minimum.</summary>
    public int? MinNights { get; init; }

    /// <summary>The nightly rate, in cents of euro.</summary>
    public long BaseRateCents { get; init; }

    /// <summary>Percent added to the rate on a Friday or Saturday night; 0 = none.</summary>
    public decimal WeekendSurchargePercent { get; init; }

    /// <summary>The cleaning fee of a stay, in cents of euro.</summary>
    public long CleaningFeeCents { get; init; }

    public static HostCalendarPropertyDto From(CalendarProperty property) => new()
    {
        Id = property.Id,
        Name = property.Name,
        City = property.City,
        RentalMode = property.RentalMode,
        Status = property.Status,
        MinNights = property.MinNights,
        BaseRateCents = property.BaseRateCents,
        WeekendSurchargePercent = property.WeekendSurchargePercent,
        CleaningFeeCents = property.CleaningFeeCents,
    };
}

/// <summary>
/// One thing on the calendar. <see cref="From"/> and <see cref="To"/> are the first night and the day of the departure (the first
/// day it no longer takes a night: a stay that leaves on the 12th and a stay that arrives on the 12th do not overlap); an intervention
/// has the day of the work and the day after. The fields below are there for the <see cref="Type"/> that says so and absent
/// (never <c>null</c>) for the others.
/// </summary>
public sealed class HostCalendarItemDto
{
    public Guid Id { get; init; }

    /// <summary><c>Booking</c> (a stay of CasaZen), <c>Ical</c> (a portal's reservation as a block of its calendar), <c>Block</c> (the host's own) or <c>Service</c> (an intervention).</summary>
    public CalendarItemType Type { get; init; }

    public Guid PropertyId { get; init; }

    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    // --- Booking -------------------------------------------------------------------------------------------

    /// <summary><c>Pending</c> (an unanswered "pay at the property" request, or a checkout being paid), <c>Confirmed</c>, <c>CheckedIn</c> or <c>CheckedOut</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BookingStatus? Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GuestName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumberOfGuests { get; init; }

    /// <summary>Where it came from: <c>Direct</c> (the booking site), <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c> or <c>Other</c>. Also on an <c>Ical</c> item: the portal of its calendar.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BookingChannel? Channel { get; init; }

    /// <summary><c>Immediate</c>, <c>OnCancellationDeadline</c> or <c>OnSite</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaymentOption? PaymentOption { get; init; }

    /// <summary>For a "pay at the property" request: <c>AwaitingGuestEmail</c> or <c>AwaitingHostApproval</c>; absent for any other booking.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OnSiteRequestState? OnSiteRequestState { get; init; }

    /// <summary>For a request: when it is cancelled if nobody answers (UTC); the calendar draws it dashed until then.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? RequestExpiresAt { get; init; }

    /// <summary><c>None</c>, <c>Partial</c>, <c>Paid</c> or <c>Refunded</c>: how much of the total came in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BookingPaymentState? PaymentState { get; init; }

    /// <summary>The total of the stay, in cents of euro.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalCents { get; init; }

    /// <summary>The calendar a portal's block was turned into this stay from; absent for every other booking.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? IcalFeedId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelLabel { get; init; }

    /// <summary>Why the portal's calendar asks to check the reservation again; absent when there is nothing to check.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OtaStayReviewReason? OtaReviewReason { get; init; }

    // --- Ical ----------------------------------------------------------------------------------------------

    /// <summary>The name the host gave to the portal's calendar.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FeedLabel { get; init; }

    /// <summary>The host can turn it into a stay with the guest's name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Convertible { get; init; }

    // --- Block ---------------------------------------------------------------------------------------------

    /// <summary><c>Owner</c>, <c>Maintenance</c>, <c>Other</c> or <c>ModeChange</c> (held by CasaZen while the property changes to long-term leases).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CalendarBlockReason? Reason { get; init; }

    // --- Ical and Block ------------------------------------------------------------------------------------

    /// <summary>The host's note on a block, or what the portal wrote on the event of an <c>Ical</c> item: for the host only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    // --- Service -------------------------------------------------------------------------------------------

    /// <summary><c>Cleaning</c>, <c>Maintenance</c> or <c>Other</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CalendarServiceKind? ServiceKind { get; init; }

    /// <summary>The category code (<c>cleaning</c>, <c>maintenance</c>, ...): the client translates it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    /// <summary>The service of the supplier's catalog it was asked as; absent for a request by category only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceName { get; init; }

    /// <summary><c>Richiesto</c>, <c>PresoInCarico</c>, <c>InCorso</c>, <c>Completato</c> or <c>Pagato</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceRequestStatus? ServiceStatus { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SupplierOrgId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupplierName { get; init; }

    /// <summary>When the work starts (UTC); absent for a request with no time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartAt { get; init; }

    /// <summary>When the work ends (UTC); absent for a request with no time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? EndAt { get; init; }

    // --- Ical and Service ----------------------------------------------------------------------------------

    /// <summary>
    /// For an <c>Ical</c> item, the stay the host turned it into when that stay does not stand for it (its dates changed on the portal);
    /// for a <c>Service</c> item, the stay the request is for.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? BookingId { get; init; }

    public static HostCalendarItemDto Of(CalendarItem item) => item switch
    {
        BookingCalendarItem booking => new HostCalendarItemDto
        {
            Id = booking.Id,
            Type = booking.Type,
            PropertyId = booking.PropertyId,
            From = booking.From,
            To = booking.To,
            Status = booking.Status,
            GuestName = booking.GuestName,
            NumberOfGuests = booking.NumberOfGuests,
            Channel = booking.Channel,
            PaymentOption = booking.PaymentOption,
            OnSiteRequestState = booking.OnSiteRequestState,
            RequestExpiresAt = booking.RequestExpiresAt,
            PaymentState = booking.PaymentState,
            TotalCents = booking.TotalCents,
            IcalFeedId = booking.IcalFeedId,
            ChannelLabel = booking.ChannelLabel,
            OtaReviewReason = booking.OtaReviewReason,
        },
        IcalCalendarItem ical => new HostCalendarItemDto
        {
            Id = ical.Id,
            Type = ical.Type,
            PropertyId = ical.PropertyId,
            From = ical.From,
            To = ical.To,
            Channel = ical.Channel,
            FeedLabel = ical.FeedLabel,
            Convertible = ical.Convertible,
            BookingId = ical.BookingId,
            Note = ical.Note,
        },
        BlockCalendarItem block => new HostCalendarItemDto
        {
            Id = block.Id,
            Type = block.Type,
            PropertyId = block.PropertyId,
            From = block.From,
            To = block.To,
            Reason = block.Reason,
            Note = block.Note,
        },
        ServiceCalendarItem service => new HostCalendarItemDto
        {
            Id = service.Id,
            Type = service.Type,
            PropertyId = service.PropertyId,
            From = service.From,
            To = service.To,
            ServiceKind = service.Kind,
            Category = service.Category,
            ServiceName = service.ServiceName,
            ServiceStatus = service.Status,
            SupplierOrgId = service.SupplierOrgId,
            SupplierName = service.SupplierName,
            BookingId = service.BookingId,
            StartAt = service.StartAt,
            EndAt = service.EndAt,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.GetType().Name, "A calendar item without a place in the answer."),
    };
}
