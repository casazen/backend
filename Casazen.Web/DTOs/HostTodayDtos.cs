using Casazen.Core.Services;
using Casazen.Web.DTOs.Compliance;

namespace Casazen.Web.DTOs;

/// <summary>
/// The host's day in one read (SR-03, <c>GET /api/dashboard/today</c>): definitions in <see cref="IHostTodayService"/> and
/// <c>docs/TECHNICAL.md</c> ("Host dashboard"). Dates are Europe/Rome calendar dates (<c>yyyy-MM-dd</c>); instants are UTC.
/// </summary>
public sealed class HostTodayDto
{
    /// <summary>Today in Europe/Rome.</summary>
    public DateOnly Today { get; init; }

    /// <summary>Confirmed stays whose check-in date is today, with how far their online check-in is.</summary>
    public HostTodayListDto<HostTodayStayDto> Arrivals { get; init; } = new();

    /// <summary>Confirmed stays whose check-out date is today.</summary>
    public HostTodayListDto<HostTodayStayDto> Departures { get; init; } = new();

    /// <summary>Check-ins still to come after today, soonest first, with how far their online check-in is.</summary>
    public HostTodayListDto<HostTodayStayDto> Upcoming { get; init; } = new();

    /// <summary>"Pay at the property" requests waiting for the host, the ones that expire first.</summary>
    public HostTodayListDto<HostTodayApprovalDto> Approvals { get; init; } = new();

    /// <summary>The things to do, most pressing first (<see cref="HostTodoItemDto.Priority"/>).</summary>
    public HostTodayListDto<HostTodoItemDto> Todo { get; init; } = new();

    public static HostTodayDto From(HostToday today) => new()
    {
        Today = DateOnly.FromDateTime(today.TodayInRome),
        Arrivals = HostTodayListDto<HostTodayStayDto>.From(today.Arrivals, HostTodayStayDto.From),
        Departures = HostTodayListDto<HostTodayStayDto>.From(today.Departures, HostTodayStayDto.From),
        Upcoming = HostTodayListDto<HostTodayStayDto>.From(today.Upcoming, HostTodayStayDto.From),
        Approvals = HostTodayListDto<HostTodayApprovalDto>.From(today.Approvals, HostTodayApprovalDto.From),
        Todo = HostTodayListDto<HostTodoItemDto>.From(today.Todo, HostTodoItemDto.From),
    };
}

/// <summary>A count and its first items.</summary>
public sealed class HostTodayListDto<T>
{
    /// <summary>All of them; <see cref="Items"/> carries only the first ones.</summary>
    public int Count { get; init; }

    public IReadOnlyList<T> Items { get; init; } = [];

    public static HostTodayListDto<T> From<TSource>(HostTodayList<TSource> list, Func<TSource, T> map) => new()
    {
        Count = list.Count,
        Items = list.Items.Select(map).ToList(),
    };
}

/// <summary>A stay of the day. No money: the Home lists who comes and goes, the amounts are on the booking.</summary>
public sealed class HostTodayStayDto
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

    /// <summary><c>Confirmed</c> (the arrival is not registered yet), <c>CheckedIn</c> or <c>CheckedOut</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Where the booking came from: <c>Direct</c> (the booking site), <c>Manual</c>, <c>Airbnb</c>, ...</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>When the host registered the arrival; <c>null</c> before.</summary>
    public DateTime? ArrivedAt { get; init; }

    /// <summary>How far the online check-in is; <c>null</c> for a departure.</summary>
    public HostTodayCheckInDto? CheckIn { get; init; }

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
    };
}

/// <summary>The online check-in of a stay: what happened to the guest's link and whether the guest data are complete.</summary>
public sealed class HostTodayCheckInDto
{
    /// <summary>
    /// <c>NotSent</c> (no usable link, or its email failed), <c>Sent</c>, <c>InProgress</c>, <c>Completed</c> or
    /// <c>Expired</c>. The link is to be sent when this is <c>NotSent</c> or <c>Expired</c> and <see cref="DataComplete"/>
    /// is false.
    /// </summary>
    public HostTodayCheckInState State { get; init; }

    /// <summary>Every guest of the stay has every field of the Alloggiati record, whoever entered them.</summary>
    public bool DataComplete { get; init; }

    public static HostTodayCheckInDto From(HostTodayCheckIn checkIn) => new()
    {
        State = checkIn.State,
        DataComplete = checkIn.DataComplete,
    };
}

/// <summary>A "pay at the property" request: accept or decline it by <see cref="RespondBy"/>, or it is cancelled.</summary>
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

    /// <summary>Until when the host may answer (UTC); then the request is cancelled.</summary>
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

/// <summary>
/// One thing to do. <see cref="Action"/> is where it leads, as a key (never a path: the web app builds the route), with the
/// ids of its target; <see cref="Missing"/> says what is still lacking, with stable codes the client translates.
/// </summary>
public sealed class HostTodoItemDto
{
    /// <summary>
    /// <c>RespondToRequest</c>, <c>ResolveAlloggiatiFailure</c>, <c>SendAlloggiati</c>, <c>CompleteGuestCheckIn</c>,
    /// <c>ReviewFailedPayment</c>, <c>CheckOut</c>, <c>ConfirmPropertyReady</c> or <c>ActivateProperty</c>.
    /// </summary>
    public HostTodoAction Action { get; init; }

    /// <summary>1 is the most pressing; the list is in this order, the nearest <see cref="DueAt"/> first inside a priority.</summary>
    public int Priority { get; init; }

    /// <summary>When it has to be done by (UTC), if something fixes it; <c>null</c> otherwise.</summary>
    public DateTime? DueAt { get; init; }

    /// <summary>Whom or what it is about: the guest, or the property to activate.</summary>
    public string Label { get; init; } = string.Empty;

    public Guid? PropertyId { get; init; }

    public string? PropertyName { get; init; }

    public Guid? BookingId { get; init; }

    /// <summary>The failed payment, for <c>ReviewFailedPayment</c>.</summary>
    public Guid? PaymentId { get; init; }

    /// <summary>What it lacks; never empty.</summary>
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
        Missing = item.Missing.Select(ComplianceMissingDto.From).ToList(),
    };
}
