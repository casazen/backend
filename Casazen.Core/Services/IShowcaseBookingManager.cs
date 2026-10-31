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

    /// <summary>
    /// Accepts the quote the supplier sent (SP-04b): the request is taken on the supplier's behalf (<c>Richiesto</c> → <c>PresoInCarico</c>) at
    /// the time of the quote, with its total as the price agreed; the time is checked again under the calendar lock; the supplier and the
    /// customer are told. <paramref name="quoteSentAt"/> is the version of the quote the customer read (<c>quote.sentAt</c>).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingQuoteErrors.NoQuote"/>, <see cref="ShowcaseBookingQuoteErrors.QuoteExpired"/>,
    /// <see cref="ShowcaseBookingErrors.SupplierUnavailable"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <c>service_request_quote_changed</c>: the supplier replaced or withdrew the quote; <c>supplier_slot_unavailable</c>: the time is gone
    /// (the quote stays: the customer can turn it down, or cancel); <c>service_request_state_changed</c>.
    /// </exception>
    Task<ShowcaseBookingView> AcceptQuoteAsync(
        ShowcaseBookingCredentials credentials,
        DateTime quoteSentAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the quote down (SP-04b): it goes, the request stays <c>Richiesto</c> at its time, the supplier has its whole time to answer
    /// again and is told. Errors as <see cref="AcceptQuoteAsync"/>, but for the time.
    /// </summary>
    Task<ShowcaseBookingView> RejectQuoteAsync(
        ShowcaseBookingCredentials credentials,
        DateTime quoteSentAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-10b-b: the customer accepts the offer the supplier sent for its request on measure: the request is taken on the supplier's behalf
    /// (<c>Richiesto</c> → <c>PresoInCarico</c>) at the time and the total of the offer, the time is checked again under the calendar lock, and both
    /// parties are told. <paramref name="offerSentAt"/> is the version of the offer the customer read (<c>offer.sentAt</c>). The booking of the
    /// credentials has to be a request on measure; any other is <see cref="ShowcaseBookingManagementErrors.NotFound"/>.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ServiceRequestOfferErrors.NoOffer"/>, <see cref="ServiceRequestOfferErrors.OfferExpired"/>, <see cref="ShowcaseBookingErrors.SupplierUnavailable"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="ServiceRequestOfferErrors.OfferChanged"/>: the supplier replaced or withdrew the offer; <c>supplier_slot_unavailable</c> /
    /// <c>supplier_commitment_conflict</c>: the time is gone (the offer stays: the customer can turn it down, or cancel); <c>service_request_state_changed</c>.
    /// </exception>
    Task<ShowcaseBookingView> AcceptOfferAsync(
        ShowcaseBookingCredentials credentials,
        DateTime offerSentAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-10b-b: the customer turns the offer down: it goes, the request stays <c>Richiesto</c> and the conversation goes on (no clock runs on
    /// the supplier); the supplier is told. Errors as <see cref="AcceptOfferAsync"/>, but for the time.
    /// </summary>
    Task<ShowcaseBookingView> DeclineOfferAsync(
        ShowcaseBookingCredentials credentials,
        DateTime offerSentAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-15c-a: the customer says "all fine" about a booking paid inside CasaZen whose work is done: the money CasaZen holds is released to
    /// the supplier at once, without waiting for the hour after the work (decision VT-D1). The customer's word stands even if the transfer
    /// cannot be made right now (the supplier's account is not ready, Stripe refuses): the job makes it. Asking again once the supplier was
    /// paid answers the booking as it is and does nothing.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ServicePaymentErrors.NotConfirmable"/>: the booking is not paid inside CasaZen, the work is not done, or the money is not
    /// in a state to release (closed, refunded, stopped by a dispute).
    /// </exception>
    Task<ShowcaseBookingView> ConfirmOkAsync(ShowcaseBookingCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-15c-c, "Segnala un problema": the customer reports a problem with a job paid inside CasaZen — the money CasaZen holds stops until a person
    /// decides (decisions VT-D1 and VT-D16). Possible from the moment the work is completed until the money is due to the supplier (the hour after
    /// the work), never after the customer said "all fine" or the supplier was paid; for a job nobody marked completed, from the end of the time agreed
    /// for it. A complaint already open answers the same booking and writes nothing. Text only: no photos.
    /// </summary>
    /// <param name="credentials">Who the customer says it is.</param>
    /// <param name="reason"><c>not_done</c>, <c>incomplete</c>, <c>poor_quality</c>, <c>not_as_described</c> or <c>other</c>.</param>
    /// <param name="text">What the customer writes, at most 1000 characters; required for <c>other</c>.</param>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ServiceDisputeErrors.NotReportable"/> (the booking is not paid inside CasaZen, or not a job that was done),
    /// <see cref="ServiceDisputeErrors.WindowClosed"/> (the time is over), <see cref="ServiceDisputeErrors.TooEarly"/> (the work is not completed and
    /// its time has not ended), <see cref="ServiceDisputeErrors.Invalid"/> (<c>fields</c>: <c>reason</c>, <c>text</c>).
    /// </exception>
    Task<ShowcaseBookingView> ReportProblemAsync(
        ShowcaseBookingCredentials credentials,
        string? reason,
        string? text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-15c-c: the customer takes its complaint back — the money is no longer stopped and the supplier is paid. Taking back a complaint that was
    /// taken back answers the same booking.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>, or <see cref="ServiceDisputeErrors.NotFound"/> (no complaint).</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="ServiceDisputeErrors.NotOpen"/>: the complaint was decided already.</exception>
    Task<ShowcaseBookingView> WithdrawProblemAsync(ShowcaseBookingCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes in the conversation of a request on measure (SP-10b): 1 to 1000 characters of plain text, at most 200 messages a request. The
    /// supplier is told by e-mail (the comune and "Nome C.", never the words), one e-mail for the messages of ten minutes. The booking of the
    /// credentials has to be a request on measure; any other is <see cref="ShowcaseBookingManagementErrors.NotFound"/>.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingManagementErrors.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ServiceRequestThreadErrors.MessageInvalid"/>, <see cref="ServiceRequestThreadErrors.Full"/>,
    /// <see cref="ShowcaseBookingErrors.SupplierUnavailable"/>.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="ServiceRequestThreadErrors.Closed"/> (refused, cancelled or done), <c>service_request_state_changed</c>.
    /// </exception>
    Task<ShowcaseBookingView> PostMessageAsync(ShowcaseBookingCredentials credentials, string? body, CancellationToken cancellationToken = default);
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

    /// <summary>The customer accepts the quote (see <see cref="IShowcaseBookingManager.AcceptQuoteAsync"/>).</summary>
    Task<ServiceRequest> AcceptQuoteAsCustomerAsync(Guid id, Guid supplierOrgId, DateTime quoteSentAt, CancellationToken cancellationToken = default);

    /// <summary>The customer turns the quote down (see <see cref="IShowcaseBookingManager.RejectQuoteAsync"/>).</summary>
    Task<ServiceRequest> RejectQuoteAsCustomerAsync(Guid id, Guid supplierOrgId, DateTime quoteSentAt, CancellationToken cancellationToken = default);

    /// <summary>The customer accepts the offer of its request on measure (see <see cref="IShowcaseBookingManager.AcceptOfferAsync"/>).</summary>
    Task<ServiceRequest> AcceptOfferAsCustomerAsync(Guid id, Guid supplierOrgId, DateTime offerSentAt, CancellationToken cancellationToken = default);

    /// <summary>The customer turns the offer of its request on measure down (see <see cref="IShowcaseBookingManager.DeclineOfferAsync"/>).</summary>
    Task<ServiceRequest> DeclineOfferAsCustomerAsync(Guid id, Guid supplierOrgId, DateTime offerSentAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// The thread of the customer's request on measure (SP-10b), with the messages of the supplier marked as read by the customer; each
    /// message is shown as it was before this reading.
    /// </summary>
    Task<ServiceRequestThread> ReadThreadAsCustomerAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>The customer writes in the thread of its request on measure (see <see cref="IShowcaseBookingManager.PostMessageAsync"/>).</summary>
    Task<ServiceRequestMessageView> PostMessageAsCustomerAsync(Guid id, Guid supplierOrgId, string? body, CancellationToken cancellationToken = default);
}
