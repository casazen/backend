using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Arrival and departure of a stay recorded by the host (CO-08, A5-08): the single domain path behind the "Registra
/// arrivo" action (<c>POST /api/bookings/{id}/check-in</c>), the check-out wizard (<c>checkout-wizard/start</c> and
/// <c>/complete</c>) and <c>POST /api/bookings/{id}/check-out</c>. The states each operation accepts are those of
/// <see cref="StayLifecycleRules"/>, so the wizard and the endpoint never disagree.
/// </summary>
/// <remarks>
/// Every transition runs under the lock of the booking that the cancellation and the host changes take (BK-02, PC-07)
/// and reads the booking again under it: two requests sent together (a double click, the app and the web) make one
/// transition and the other gets a 409. Callers authorize the booking first (TN-3): the service never checks roles.
/// Errors are <see cref="Exceptions.DomainRuleException"/> (422), <see cref="Exceptions.DomainConflictException"/> (409)
/// and <see cref="Exceptions.NotFoundException"/> with the codes of <see cref="BookingErrorCodes"/>.
/// </remarks>
public interface IStayLifecycleService
{
    /// <summary>
    /// "Registra arrivo": a <see cref="BookingStatus.Confirmed"/> booking becomes <see cref="BookingStatus.CheckedIn"/>,
    /// from its check-in day to its check-out day (Europe/Rome). A late registration is not an error. The Alloggiati
    /// communication is scheduled (the check-out reminder comes from the stay-alerts job, CO-10). Incomplete guest data
    /// never block the arrival: the result says whether they are complete, so the host can be sent to complete them.
    /// </summary>
    Task<StayArrivalResult> RegisterArrivalAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start of the check-out wizard: checks that the stay can be checked out and records when the wizard was opened.
    /// With <paramref name="registerArrival"/> a confirmed booking whose arrival was never registered gets it first, in
    /// the same transaction ("registra arrivo e procedi"); without it such a booking answers 409
    /// <see cref="BookingErrorCodes.ArrivalNotRegistered"/>.
    /// </summary>
    Task<Booking> StartCheckOutAsync(Guid bookingId, bool registerArrival, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check-out of the stay: <see cref="BookingStatus.CheckedOut"/> (no more check-out reminder), retention of the
    /// guest data extended, optional turnover request to a supplier created in the same transaction. Same states as
    /// <see cref="StartCheckOutAsync"/>.
    /// </summary>
    Task<Booking> CheckOutAsync(Guid bookingId, StayCheckOut checkOut, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of the arrival registration.</summary>
/// <param name="Booking">The booking, now checked in.</param>
/// <param name="GuestDataComplete">
/// Whether the data of every guest of the stay are complete for the Alloggiati communication (CO-12). When false the
/// arrival is still registered: the host completes them afterwards.
/// </param>
public sealed record StayArrivalResult(Booking Booking, bool GuestDataComplete);

/// <summary>What the host confirms at check-out.</summary>
/// <param name="RegisterArrival">
/// The host confirms that the guest did arrive: a confirmed booking whose arrival was never registered is checked in
/// and out in the same transaction.
/// </param>
/// <param name="Turnover">Optional turnover request to a supplier (cleaning by default).</param>
public sealed record StayCheckOut(bool RegisterArrival, StayTurnoverRequest? Turnover = null);

/// <summary>Turnover request created with the check-out, on behalf of <paramref name="UserId"/>.</summary>
public sealed record StayTurnoverRequest(string UserId, Guid SupplierOrgId, string? Category, string? Notes);
