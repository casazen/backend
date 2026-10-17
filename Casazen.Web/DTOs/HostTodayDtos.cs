using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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

    /// <summary>
    /// The interventions (service requests) of today in Europe/Rome, in order of time (SR-03b): the ones scheduled for today and, for the
    /// ones with no time, those of a stay that leaves today. Not the ones refused or cancelled.
    /// </summary>
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

    /// <summary>
    /// Where the Alloggiati Web communication stands and, until it is sent, its legal term (SR-03b); <c>null</c> for a departure.
    /// </summary>
    public HostTodayAlloggiatiDto? Alloggiati { get; init; }

    /// <summary>The latest cleaning request tied to the stay and where it stands (SR-03b); <c>null</c> for an arrival and for a departure with none.</summary>
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

/// <summary>Where the Alloggiati Web communication of an arriving stay stands (SR-03b).</summary>
public sealed class HostTodayAlloggiatiDto
{
    /// <summary>
    /// <c>DaInviare</c> (before the arrival day), <c>DaInviareManualmente</c> (from the arrival day: the host sends it on the Questura
    /// portal), <c>Inviato</c>, <c>InviatoManualmente</c>, <c>Errore</c> or <c>Rifiutato</c>: the stored status, read as the Alloggiati
    /// page reads it.
    /// </summary>
    public AlloggiatiWebStatus Status { get; init; }

    /// <summary>
    /// The legal term of the communication (UTC): 24 hours from the arrival, 6 for a stay of one night or less; <c>null</c> once it is
    /// sent.
    /// </summary>
    public DateTime? DeadlineAt { get; init; }

    public static HostTodayAlloggiatiDto From(HostTodayAlloggiati alloggiati) => new()
    {
        Status = alloggiati.Status,
        DeadlineAt = alloggiati.DeadlineAt,
    };
}

/// <summary>The cleaning of a leaving stay (SR-03b): its latest request and where it stands.</summary>
public sealed class HostTodayCleaningDto
{
    public Guid ServiceRequestId { get; init; }

    /// <summary><c>Richiesto</c>, <c>PresoInCarico</c>, <c>InCorso</c>, <c>Completato</c>, <c>Pagato</c>, <c>Rifiutato</c> or <c>Annullato</c>.</summary>
    public ServiceRequestStatus Status { get; init; }

    public static HostTodayCleaningDto From(HostTodayCleaning cleaning) => new()
    {
        ServiceRequestId = cleaning.ServiceRequestId,
        Status = cleaning.Status,
    };
}

/// <summary>An intervention (service request) of the day (SR-03b).</summary>
public sealed class HostTodayServiceRequestDto
{
    public Guid Id { get; init; }

    /// <summary>The category code (<c>cleaning</c>, <c>maintenance</c>, ...): the client translates it.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>The name of the service of the supplier's catalog when the request was made; <c>null</c> for a request by category only.</summary>
    public string? ServiceName { get; init; }

    /// <summary>When the work starts (UTC); <c>null</c> for a request with no time.</summary>
    public DateTime? ScheduledStart { get; init; }

    /// <summary>When the work ends (UTC); <c>null</c> for a request with no time.</summary>
    public DateTime? ScheduledEnd { get; init; }

    /// <summary><c>Richiesto</c>, <c>PresoInCarico</c>, <c>InCorso</c>, <c>Completato</c> or <c>Pagato</c>.</summary>
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
    /// <c>ReviewFailedPayment</c>, <c>CheckOut</c>, <c>ConfirmPropertyReady</c>, <c>ActivateProperty</c> and, about an intervention
    /// (SR-03b), <c>RespondToServiceRequest</c>, <c>ConfirmFinalAmount</c> or <c>PayServiceRequest</c>, and, about the calendar (SR-02a),
    /// <c>ResolveCalendarConflict</c> (two stays or blocks of a property take the same night).
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

    /// <summary>The intervention (service request), for <c>RespondToServiceRequest</c>, <c>ConfirmFinalAmount</c> and <c>PayServiceRequest</c> (SR-03b).</summary>
    public Guid? ServiceRequestId { get; init; }

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
        ServiceRequestId = item.ServiceRequestId,
        Missing = item.Missing.Select(ComplianceMissingDto.From).ToList(),
    };
}
