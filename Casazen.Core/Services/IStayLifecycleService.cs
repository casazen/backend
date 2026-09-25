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
    /// Start of the check-out wizard: checks that the stay can be checked out, records when the wizard was opened and
    /// creates its <see cref="StayCheckout"/> (progress of the wizard, CO-17) if there is none yet. Starting again keeps
    /// the progress. With <paramref name="registerArrival"/> a confirmed booking whose arrival was never registered gets
    /// it first, in the same transaction ("registra arrivo e procedi"); without it such a booking answers 409
    /// <see cref="BookingErrorCodes.ArrivalNotRegistered"/>.
    /// </summary>
    Task<Booking> StartCheckOutAsync(Guid bookingId, bool registerArrival, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check-out of the stay: <see cref="BookingStatus.CheckedOut"/> (no more check-out reminder), retention of the
    /// guest data extended, optional turnover request to a supplier created in the same transaction, and the
    /// <see cref="StayCheckout"/> of the stay closed with what the host declared (<see cref="StayCheckOut.Wizard"/>).
    /// Same states as <see cref="StartCheckOutAsync"/>; from the wizard (<see cref="StayCheckOut.Wizard"/> set) the
    /// wizard must have been started, otherwise 409 <see cref="BookingErrorCodes.CheckoutWizardNotStarted"/>.
    /// </summary>
    Task<Booking> CheckOutAsync(Guid bookingId, StayCheckOut checkOut, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the progress of the check-out wizard (CO-17): the step the host is on and the answers given so far. Nothing
    /// is created or declared here: the check-out does it. 409 <see cref="BookingErrorCodes.CheckoutWizardNotStarted"/>
    /// before the start, <see cref="BookingErrorCodes.AlreadyCheckedOut"/> once the stay is closed.
    /// </summary>
    Task<StayCheckout> SaveCheckoutProgressAsync(
        Guid bookingId,
        StayCheckoutProgress progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The host declares ready, after the check-out, the property of a stay closed with "not ready yet" (the turnover of
    /// the cockpit). Idempotent: a property already declared ready keeps its first declaration. 409
    /// <see cref="BookingErrorCodes.CheckoutNotCompleted"/> while the stay is not checked out.
    /// </summary>
    Task<StayCheckout> ConfirmPropertyReadyAsync(Guid bookingId, string? notes, CancellationToken cancellationToken = default);
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
public sealed record StayCheckOut(bool RegisterArrival, StayTurnoverRequest? Turnover = null)
{
    /// <summary>
    /// Set by the check-out wizard (CO-17): what the host declared in its steps. The wizard must have been started.
    /// Null for <c>POST /api/bookings/{id}/check-out</c>, which declares nothing: the property is not ready until the
    /// host says so.
    /// </summary>
    public StayCheckoutDeclaration? Wizard { get; init; }
}

/// <summary>What the host declared in the check-out wizard (CO-17, A5-24).</summary>
/// <param name="CleaningSkipped">
/// Step 3: the host chose not to request a cleaning. Never together with <see cref="StayCheckOut.Turnover"/>.
/// </param>
/// <param name="TouristTaxCollection">Step 4: how the tourist tax was collected; null = not declared.</param>
/// <param name="PropertyReady">Step 5: the property is ready for the next guest.</param>
/// <param name="PropertyNotes">Step 5: notes on the state of the property.</param>
public sealed record StayCheckoutDeclaration(
    bool CleaningSkipped,
    TouristTaxCollection? TouristTaxCollection,
    bool PropertyReady,
    string? PropertyNotes);

/// <summary>Progress of the check-out wizard, saved as the host moves between the steps (CO-17).</summary>
/// <param name="CurrentStep">The step the host is on.</param>
/// <param name="DepartureConfirmed">Step 1: the guest left.</param>
/// <param name="CleaningChoice">Step 3: request or skip; null = not chosen yet.</param>
/// <param name="CleaningSupplierOrgId">Step 3: chosen supplier (ignored when skipped).</param>
/// <param name="CleaningCategory">Step 3: category code of the request (ignored when skipped).</param>
/// <param name="CleaningNotes">Step 3: notes for the supplier (ignored when skipped).</param>
/// <param name="TouristTaxCollection">Step 4: how the tax was collected; null = not answered yet.</param>
/// <param name="PropertyReady">Step 5: ready or not yet; null = not answered yet.</param>
/// <param name="PropertyNotes">Step 5: notes on the state of the property.</param>
public sealed record StayCheckoutProgress(
    CheckoutWizardStep CurrentStep,
    bool DepartureConfirmed,
    CheckoutCleaningChoice? CleaningChoice,
    Guid? CleaningSupplierOrgId,
    string? CleaningCategory,
    string? CleaningNotes,
    TouristTaxCollection? TouristTaxCollection,
    bool? PropertyReady,
    string? PropertyNotes);

/// <summary>Turnover request created with the check-out, on behalf of <paramref name="UserId"/>.</summary>
public sealed record StayTurnoverRequest(string UserId, Guid SupplierOrgId, string? Category, string? Notes);
