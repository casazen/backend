using Casazen.Core.Entities;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The customer's own area of a booking made from a supplier's public showcase (SP-11, decision D34 revised): with the code of the
/// booking and the e-mail address it was made with — nothing else, the customer has no account — the customer finds its booking
/// again, cancels it, moves it to another time while the supplier has not answered, and answers a time the supplier proposed.
/// Every operation answers with the booking as it is afterwards (<see cref="ShowcaseBookingView"/>).
/// </summary>
/// <remarks>
/// <para><b>One answer for every failure to identify.</b> A supplier that does not exist, a code that does not exist or has a wrong
/// format, a code of another supplier and an address that is not the one of the booking are all
/// <see cref="ShowcaseBookingManagementErrors.BookingNotFound"/> — the same 404 with the same body — and every one of them runs the
/// same statements before it fails, so neither the answer nor its cost tells which it was. The address is compared after the
/// stored one was decrypted, in constant time (<see cref="ServiceCustomerEmails.SameAddress"/>), and only once the booking has been
/// found by its code. Nothing a customer typed is written to a log.</para>
/// <para><b>What the customer never gets:</b> the data of another customer, what the supplier noted for itself, and — before the
/// supplier took the request — the exact address and the notes for the access (<see cref="ShowcaseBookingView"/>).</para>
/// <para><b>Concurrency.</b> The actions are those of <see cref="IShowcaseRequestCustomerActions"/>: every change is saved only if
/// nobody changed the request since it was read (<c>xmin</c>), so of the customer and the supplier acting at the same moment one
/// wins and the other gets 409 <c>service_request_state_changed</c>; the time of a booking is changed under the supplier's calendar
/// lock, judged by the slot planner after taking it (409 <c>supplier_slot_unavailable</c>). Never a 500.</para>
/// </remarks>
public interface IShowcaseBookingManager
{
    /// <summary>The booking of the credentials, with what the customer can do with it. Read-only.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>: no booking matches (see the remarks).</exception>
    Task<ShowcaseBookingView> LookupAsync(ShowcaseBookingCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the booking (<c>Richiesto</c> or <c>PresoInCarico</c> → <c>Annullato</c>, by the customer), with an optional reason of
    /// at most 500 characters. Free until <c>Suppliers:Showcase:FreeCancellationHours</c> before the work, and without charge after
    /// it (decision D6). The slot is free again; the supplier and the customer are told. A second call of a booking the customer
    /// already cancelled answers the same booking and does nothing.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingManagementErrors.CannotCancel"/>, or <see cref="ShowcaseBookingErrors.Invalid"/> for a reason that is
    /// not valid (<see cref="ShowcaseBookingRuleException"/>).
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException"><c>service_request_state_changed</c>: the supplier acted at the same moment.</exception>
    Task<ShowcaseBookingView> CancelAsync(
        ShowcaseBookingCredentials credentials,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a new request to another slot of the same service (<c>GET api/public/suppliers/{slug}/slots</c>): the old slot is
    /// freed, a proposal of the supplier is dropped, and the supplier has its whole time to answer again
    /// (<c>SupplierSettings.RespondWithinMinutes</c>) and is told. Only while the request is <c>Richiesto</c>. The same time again
    /// changes nothing.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingManagementErrors.CannotReschedule"/>, <see cref="ShowcaseBookingErrors.SupplierUnavailable"/>, or
    /// <see cref="ShowcaseBookingErrors.Invalid"/> for a start that is not a whole minute.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <c>supplier_slot_unavailable</c>: the slot is not free (anymore); <c>service_request_state_changed</c>: the supplier acted at
    /// the same moment.
    /// </exception>
    Task<ShowcaseBookingView> RescheduleAsync(
        ShowcaseBookingCredentials credentials,
        DateTime startUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts the time the supplier proposed: the request is taken on the supplier's behalf at that time
    /// (<c>Richiesto</c> → <c>PresoInCarico</c>), the slot is checked again under the calendar lock; the supplier and the customer
    /// are told.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingManagementErrors.NoProposal"/>, <see cref="ShowcaseBookingManagementErrors.ProposalExpired"/>,
    /// <see cref="ShowcaseBookingErrors.SupplierUnavailable"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <c>supplier_slot_unavailable</c>: the proposed time is gone (the proposal stays); <c>service_request_state_changed</c>.
    /// </exception>
    Task<ShowcaseBookingView> AcceptProposalAsync(ShowcaseBookingCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the proposed time down: the proposal goes, the request stays <c>Richiesto</c> at its time and the supplier has its whole
    /// time to answer again; the supplier is told.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingManagementErrors.NoProposal"/>, <see cref="ShowcaseBookingManagementErrors.ProposalExpired"/>,
    /// <see cref="ShowcaseBookingErrors.SupplierUnavailable"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException"><c>service_request_state_changed</c>.</exception>
    Task<ShowcaseBookingView> RejectProposalAsync(ShowcaseBookingCredentials credentials, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the customer of a request from a supplier's public showcase does to the request (SP-11), implemented with the other
/// transitions of a request by <c>ServiceRequestService</c>: the same state machine, the same <c>xmin</c> check, the same calendar
/// lock and slot planner as the supplier's and the host's.
/// </summary>
/// <remarks>
/// <b>The caller has identified the customer.</b> These methods take the id of the request and of the supplier org and trust
/// them: there is no code and no e-mail here. The only caller is <see cref="IShowcaseBookingManager"/>, which proves that the
/// customer knows both; an architecture test keeps it that way. A request that is not a showcase request of that supplier is
/// <see cref="ShowcaseBookingManagementErrors.NotFound"/>.
/// </remarks>
public interface IShowcaseRequestCustomerActions
{
    /// <summary>
    /// The customer cancels its request (<see cref="ShowcaseBookingManagementRules.CanCancel"/>), under the supplier's calendar lock:
    /// <c>Annullato</c> by <see cref="Entities.Enums.ServiceRequestActorParty.Customer"/>, with the reason the customer wrote or
    /// <see cref="ServiceRequestCancellationReasons.CancelledByCustomer"/>. A request the customer already cancelled is returned
    /// as it is.
    /// </summary>
    Task<ServiceRequest> CancelAsCustomerAsync(Guid id, Guid supplierOrgId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>The customer moves a new request to the slot starting at <paramref name="startUtc"/> (see <see cref="IShowcaseBookingManager.RescheduleAsync"/>).</summary>
    Task<ServiceRequest> RescheduleAsCustomerAsync(Guid id, Guid supplierOrgId, DateTime startUtc, CancellationToken cancellationToken = default);

    /// <summary>The customer accepts the proposed time (see <see cref="IShowcaseBookingManager.AcceptProposalAsync"/>).</summary>
    Task<ServiceRequest> AcceptProposalAsCustomerAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>The customer turns the proposed time down (see <see cref="IShowcaseBookingManager.RejectProposalAsync"/>).</summary>
    Task<ServiceRequest> RejectProposalAsCustomerAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);
}
