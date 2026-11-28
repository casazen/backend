using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class HostCalendarDto
{
    public string Timezone { get; init; } = string.Empty;

    public int UtcOffsetMinutes { get; init; }

    public DateOnly Today { get; init; }

    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public int PropertiesTotal { get; init; }

    public IReadOnlyList<HostCalendarPropertyDto> Properties { get; init; } = [];

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

public sealed class HostCalendarPropertyDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public RentalMode RentalMode { get; init; }

    public CalendarPropertyStatus Status { get; init; }

    public int? MinNights { get; init; }

    public long BaseRateCents { get; init; }

    public decimal WeekendSurchargePercent { get; init; }

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

    public CalendarItemType Type { get; init; }

    public Guid PropertyId { get; init; }

    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BookingStatus? Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GuestName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumberOfGuests { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BookingChannel? Channel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaymentOption? PaymentOption { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OnSiteRequestState? OnSiteRequestState { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? RequestExpiresAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BookingPaymentState? PaymentState { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalCents { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? IcalFeedId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelLabel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OtaStayReviewReason? OtaReviewReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FeedLabel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Convertible { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CalendarBlockReason? Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CalendarServiceKind? ServiceKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceRequestStatus? ServiceStatus { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SupplierOrgId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupplierName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? EndAt { get; init; }

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
