using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Compliance;

namespace Casazen.Web.DTOs;

public sealed class HostTodayDto
{
    public DateOnly Today { get; init; }

    public HostTodayListDto<HostTodayStayDto> Arrivals { get; init; } = new();

    public HostTodayListDto<HostTodayStayDto> Departures { get; init; } = new();

    public HostTodayListDto<HostTodayStayDto> Upcoming { get; init; } = new();

    public HostTodayListDto<HostTodayApprovalDto> Approvals { get; init; } = new();

    public HostTodayListDto<HostTodoItemDto> Todo { get; init; } = new();

    public HostTodayListDto<HostTodayServiceRequestDto> Services { get; init; } = new();

    public static HostTodayDto From(HostToday today) => new()
    {
        Today = DateOnly.FromDateTime(today.TodayInRome),
        Arrivals = HostTodayListDto<HostTodayStayDto>.From(today.Arrivals, HostTodayStayDto.From),
        Departures = HostTodayListDto<HostTodayStayDto>.From(today.Departures, HostTodayStayDto.From),
        Upcoming = HostTodayListDto<HostTodayStayDto>.From(today.Upcoming, HostTodayStayDto.From),
        Approvals = HostTodayListDto<HostTodayApprovalDto>.From(today.Approvals, HostTodayApprovalDto.From),
        Todo = HostTodayListDto<HostTodoItemDto>.From(today.Todo, HostTodoItemDto.From),
        Services = HostTodayListDto<HostTodayServiceRequestDto>.From(today.Services, HostTodayServiceRequestDto.From),
    };
}

public sealed class HostTodayListDto<T>
{
    public int Count { get; init; }

    public IReadOnlyList<T> Items { get; init; } = [];

    public static HostTodayListDto<T> From<TSource>(HostTodayList<TSource> list, Func<TSource, T> map) => new()
    {
        Count = list.Count,
        Items = list.Items.Select(map).ToList(),
    };
}

public sealed class HostTodayStayDto
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

    public DateTime? ArrivedAt { get; init; }

    public HostTodayCheckInDto? CheckIn { get; init; }

    /// <summary>
    /// Where the Alloggiati Web communication stands and, until it is sent, its legal term (SR-03b); <c>null</c> for a departure and for a
    /// no-show, which owes no communication (NF-45).
    /// </summary>
    public HostTodayAlloggiatiDto? Alloggiati { get; init; }

    public HostTodayCleaningDto? Cleaning { get; init; }

    public static HostTodayStayDto From(HostTodayStay item) => new()
    {
        BookingId = item.Stay.BookingId,
        BookingCode = BookingCodes.Format(item.Stay.BookingCode),
        PropertyId = item.Stay.PropertyId,
        PropertyName = item.Stay.PropertyName,
        GuestName = item.Stay.GuestName,
        NumberOfGuests = item.Stay.NumberOfGuests,
        CheckInDate = DateOnly.FromDateTime(item.Stay.CheckInDate),
        CheckOutDate = DateOnly.FromDateTime(item.Stay.CheckOutDate),
        Status = item.Stay.Status.ToString(),
        Source = item.Stay.Source.ToString(),
        ArrivedAt = item.Stay.ArrivedAt,
        CheckIn = item.CheckIn is null ? null : HostTodayCheckInDto.From(item.CheckIn),
        Alloggiati = item.Alloggiati is null ? null : HostTodayAlloggiatiDto.From(item.Alloggiati),
        Cleaning = item.Cleaning is null ? null : HostTodayCleaningDto.From(item.Cleaning),
    };
}

public sealed class HostTodayCheckInDto
{
    public HostTodayCheckInState State { get; init; }

    /// <summary>Every guest of the stay has every field of the Alloggiati record, whoever entered them.</summary>
    public bool DataComplete { get; init; }

    public static HostTodayCheckInDto From(HostTodayCheckIn checkIn) => new()
    {
        State = checkIn.State,
        DataComplete = checkIn.DataComplete,
    };
}

public sealed class HostTodayAlloggiatiDto
{
    /// <summary>
    /// <c>DaInviare</c> (before the arrival day), <c>DaInviareManualmente</c> (from the arrival day: the host sends it on the Questura
    /// portal), <c>Inviato</c>, <c>InviatoManualmente</c>, <c>Errore</c> or <c>Rifiutato</c>: the stored status, read as the Alloggiati
    /// page reads it.
    /// </summary>
    public AlloggiatiWebStatus Status { get; init; }

    public DateTime? DeadlineAt { get; init; }

    public static HostTodayAlloggiatiDto From(HostTodayAlloggiati alloggiati) => new()
    {
        Status = alloggiati.Status,
        DeadlineAt = alloggiati.DeadlineAt,
    };
}

public sealed class HostTodayCleaningDto
{
    public Guid ServiceRequestId { get; init; }

    public ServiceRequestStatus Status { get; init; }

    public static HostTodayCleaningDto From(HostTodayCleaning cleaning) => new()
    {
        ServiceRequestId = cleaning.ServiceRequestId,
        Status = cleaning.Status,
    };
}

public sealed class HostTodayServiceRequestDto
{
    public Guid Id { get; init; }

    public string Category { get; init; } = string.Empty;

    public string? ServiceName { get; init; }

    public DateTime? ScheduledStart { get; init; }

    public DateTime? ScheduledEnd { get; init; }

    public ServiceRequestStatus Status { get; init; }

    public Guid SupplierOrgId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public Guid? BookingId { get; init; }

    public static HostTodayServiceRequestDto From(HostTodayServiceRequest request) => new()
    {
        Id = request.Id,
        Category = request.Category,
        ServiceName = request.ServiceName,
        ScheduledStart = request.ScheduledStart,
        ScheduledEnd = request.ScheduledEnd,
        Status = request.Status,
        SupplierOrgId = request.SupplierOrgId,
        SupplierName = request.SupplierName,
        PropertyId = request.PropertyId,
        PropertyName = request.PropertyName,
        BookingId = request.BookingId,
    };
}

public sealed class HostTodayApprovalDto
{
    public Guid BookingId { get; init; }

    public string BookingCode { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string GuestName { get; init; } = string.Empty;

    public int NumberOfGuests { get; init; }

    public DateOnly CheckInDate { get; init; }

    public DateOnly CheckOutDate { get; init; }

    public DateTime RespondBy { get; init; }

    public static HostTodayApprovalDto From(HostTodayApproval approval) => new()
    {
        BookingId = approval.BookingId,
        BookingCode = BookingCodes.Format(approval.BookingCode),
        PropertyId = approval.PropertyId,
        PropertyName = approval.PropertyName,
        GuestName = approval.GuestName,
        NumberOfGuests = approval.NumberOfGuests,
        CheckInDate = DateOnly.FromDateTime(approval.CheckInDate),
        CheckOutDate = DateOnly.FromDateTime(approval.CheckOutDate),
        RespondBy = approval.RespondBy,
    };
}

public sealed class HostTodoItemDto
{
    public HostTodoAction Action { get; init; }

    public int Priority { get; init; }

    /// <summary>When it has to be done by (UTC), if something fixes it; <c>null</c> otherwise.</summary>
    public DateTime? DueAt { get; init; }

    public string Label { get; init; } = string.Empty;

    public Guid? PropertyId { get; init; }

    public string? PropertyName { get; init; }

    public Guid? BookingId { get; init; }

    public Guid? PaymentId { get; init; }

    public Guid? ServiceRequestId { get; init; }

    public IReadOnlyList<ComplianceMissingDto> Missing { get; init; } = [];

    public static HostTodoItemDto From(HostTodoItem item) => new()
    {
        Action = item.Action,
        Priority = item.Priority,
        DueAt = item.DueAt,
        Label = item.Label,
        PropertyId = item.PropertyId,
        PropertyName = item.PropertyName,
        BookingId = item.BookingId,
        PaymentId = item.PaymentId,
        ServiceRequestId = item.ServiceRequestId,
        Missing = item.Missing.Select(ComplianceMissingDto.From).ToList(),
    };
}
