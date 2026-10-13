using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The host's day in one read (SR-03, <c>GET /api/dashboard/today</c>): who arrives and who leaves today (Europe/Rome), what
/// comes next, the requests waiting for an answer, and the things to do, most pressing first, each with what it lacks and
/// where to go. Limited to the caller's <see cref="HostScope"/> in SQL, like every list of the host.
/// </summary>
/// <remarks>
/// It adds no rule of its own: the stays are <see cref="IHostDashboardService.GetTodayStaysAsync"/> (<see cref="StayKpiRules"/>),
/// the requests <see cref="IOnSiteBookingRequestService.GetAwaitingHostApprovalAsync"/>, the compliance duties the cockpit
/// (<see cref="IComplianceWizardService.GetSummaryAsync"/>) described by <see cref="IComplianceMissingService"/>. Only the
/// failed payments are read here, and only for a caller who may read payments (<see cref="HostTodayOptions.IncludePayments"/>).
/// The order of the things to do is <see cref="HostTodoPriority"/>.
/// </remarks>
public interface IHostTodayService
{
    Task<HostToday> GetTodayAsync(HostScope scope, HostTodayOptions options, CancellationToken cancellationToken = default);
}

/// <summary>What the caller may see of the day (SR-03).</summary>
/// <param name="IncludePayments">
/// Also list the confirmed stays whose payment failed (<see cref="HostTodoAction.ReviewFailedPayment"/>). Payment data: the
/// web layer sets it only for a caller who may read payments.
/// </param>
public sealed record HostTodayOptions(bool IncludePayments = false);

/// <summary>A count and its first items (the count covers all of them).</summary>
public sealed record HostTodayList<T>(int Count, IReadOnlyList<T> Items);

/// <summary>The day of the host.</summary>
/// <param name="TodayInRome">Today in Europe/Rome, as midnight UTC of the date.</param>
/// <param name="Arrivals">Confirmed stays whose check-in date is today, registered or not, with their check-in state.</param>
/// <param name="Departures">Confirmed stays whose check-out date is today.</param>
/// <param name="Upcoming">Check-ins still to come after today, soonest first, with their check-in state.</param>
/// <param name="Approvals">"Pay at the property" requests waiting for the host, the ones to expire first.</param>
/// <param name="Todo">The things to do, most pressing first (<see cref="HostTodoPriority"/>).</param>
public sealed record HostToday(
    DateTime TodayInRome,
    HostTodayList<HostTodayStay> Arrivals,
    HostTodayList<HostTodayStay> Departures,
    HostTodayList<HostTodayStay> Upcoming,
    HostTodayList<HostTodayApproval> Approvals,
    HostTodayList<HostTodoItem> Todo);

/// <summary>A stay of the day. <paramref name="CheckIn"/> is null for a departure, which has no check-in to follow.</summary>
public sealed record HostTodayStay(HostDashboardStay Stay, HostTodayCheckIn? CheckIn);

/// <summary>How far the online check-in of a stay is (the guest's link and the data of the guests).</summary>
/// <param name="State">What happened to the link.</param>
/// <param name="DataComplete">
/// Every guest has every field of the Alloggiati record (<see cref="Casazen.Core.Regulatory.AlloggiatiRecordRules.IsDataComplete"/>),
/// whoever entered them: when it is true no link is needed.
/// </param>
public sealed record HostTodayCheckIn(HostTodayCheckInState State, bool DataComplete);

/// <summary>What happened to the check-in link of a stay.</summary>
public enum HostTodayCheckInState
{
    /// <summary>No usable link was issued, or the email with it failed: the guest has nothing to open.</summary>
    NotSent,

    /// <summary>A link is out and the guest has not opened it yet.</summary>
    Sent,

    /// <summary>The guest opened the link and is filling in the form.</summary>
    InProgress,

    /// <summary>The guest submitted the data.</summary>
    Completed,

    /// <summary>The link expired before the guest submitted the data.</summary>
    Expired,
}

/// <summary>A "pay at the property" request waiting for the host, whose answer is due by <paramref name="RespondBy"/>.</summary>
/// <param name="CheckInDate">Europe/Rome check-in date, midnight UTC.</param>
/// <param name="CheckOutDate">Europe/Rome check-out date, midnight UTC.</param>
/// <param name="RespondBy">Until when the host may answer (UTC): then the request is cancelled.</param>
public sealed record HostTodayApproval(
    Guid BookingId,
    string BookingCode,
    Guid PropertyId,
    string PropertyName,
    string GuestName,
    int NumberOfGuests,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    DateTime RespondBy);

/// <summary>
/// Where a thing to do leads, as a key and never a path (the web app builds the route from its <c>ROUTE_MANIFEST</c>, like for
/// <see cref="Casazen.Core.Enums.ComplianceCockpitAction"/>, whose values the first ones repeat). Serialized by name: rename
/// or remove a value only together with the clients.
/// </summary>
public enum HostTodoAction
{
    /// <summary>A request to accept or decline. Target: the booking.</summary>
    RespondToRequest,

    /// <summary>The Alloggiati communication failed or was rejected (<c>ResolveAlloggiatiFailure</c>). Target: the booking.</summary>
    ResolveAlloggiatiFailure,

    /// <summary>The Alloggiati communication is to be sent on the portal (<c>SendAlloggiati</c>). Target: the booking.</summary>
    SendAlloggiati,

    /// <summary>The guest data of the stay are incomplete, or the check-in link is to be sent (<c>CompleteGuestCheckIn</c>). Target: the booking.</summary>
    CompleteGuestCheckIn,

    /// <summary>The payment of a confirmed stay failed. Target: the booking (and the payment).</summary>
    ReviewFailedPayment,

    /// <summary>A departure to close (<c>CheckOut</c>). Target: the booking.</summary>
    CheckOut,

    /// <summary>A property to declare ready after the check-out (<c>ConfirmPropertyReady</c>). Target: the booking.</summary>
    ConfirmPropertyReady,

    /// <summary>A property to activate (<c>ActivateProperty</c>). Target: the property.</summary>
    ActivateProperty,
}

/// <summary>One thing to do.</summary>
/// <param name="Action">Where it leads (<see cref="HostTodoAction"/>).</param>
/// <param name="Priority">1 is the most pressing (<see cref="HostTodoPriority.Of"/>).</param>
/// <param name="DueAt">When it has to be done by (UTC), if something fixes it: the answer of a request, the legal term of the Alloggiati communication, the day of the arrival or of the departure.</param>
/// <param name="Label">Whom or what it is about: the guest, or the property to activate.</param>
/// <param name="Missing">What it lacks, with stable codes and fields; never empty.</param>
public sealed record HostTodoItem(
    HostTodoAction Action,
    int Priority,
    DateTime? DueAt,
    string Label,
    Guid? PropertyId,
    string? PropertyName,
    Guid? BookingId,
    Guid? PaymentId,
    IReadOnlyList<ComplianceMissing> Missing);

/// <summary>
/// The order of the things to do (SR-03), in one place. A request comes first: it is cancelled by itself when its time runs
/// out. Then the legal duties of the stays (a communication that failed, one to send), the data the guest has to give before
/// arriving, the money that did not come, the departures to close, the properties to set ready, and last the properties to
/// activate, which wait for the host's time. Inside the same priority the nearest term comes first.
/// </summary>
public static class HostTodoPriority
{
    /// <summary>The priority of an action, 1 (most pressing) to 8.</summary>
    public static int Of(HostTodoAction action) => action switch
    {
        HostTodoAction.RespondToRequest => 1,
        HostTodoAction.ResolveAlloggiatiFailure => 2,
        HostTodoAction.SendAlloggiati => 3,
        HostTodoAction.CompleteGuestCheckIn => 4,
        HostTodoAction.ReviewFailedPayment => 5,
        HostTodoAction.CheckOut => 6,
        HostTodoAction.ConfirmPropertyReady => 7,
        HostTodoAction.ActivateProperty => 8,
        _ => 9,
    };

    /// <summary>The action of a cockpit item (<see cref="Casazen.Core.Enums.ComplianceCockpitAction"/>), by name.</summary>
    public static HostTodoAction From(Casazen.Core.Enums.ComplianceCockpitAction action) => action switch
    {
        Casazen.Core.Enums.ComplianceCockpitAction.ActivateProperty => HostTodoAction.ActivateProperty,
        Casazen.Core.Enums.ComplianceCockpitAction.CompleteGuestCheckIn => HostTodoAction.CompleteGuestCheckIn,
        Casazen.Core.Enums.ComplianceCockpitAction.CheckOut => HostTodoAction.CheckOut,
        Casazen.Core.Enums.ComplianceCockpitAction.SendAlloggiati => HostTodoAction.SendAlloggiati,
        Casazen.Core.Enums.ComplianceCockpitAction.ResolveAlloggiatiFailure => HostTodoAction.ResolveAlloggiatiFailure,
        Casazen.Core.Enums.ComplianceCockpitAction.ConfirmPropertyReady => HostTodoAction.ConfirmPropertyReady,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "A cockpit action without a place in the things to do."),
    };
}
