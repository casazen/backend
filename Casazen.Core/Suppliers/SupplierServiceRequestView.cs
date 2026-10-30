using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// A service request as the supplier it was sent to sees it (SU-08, A4-14; SP-04): where and when the job is, the price,
/// and the host contact once the supplier took it. Built only from the fields listed here, so no guest data (name, email,
/// document, phone) can ever reach the supplier: the stay is given by its dates only.
/// </summary>
/// <param name="Notes">
/// The host's notes for the supplier: <b>null until the supplier takes the request</b> (decision D9, they may name a person or
/// a door code), like the street address and the contact. A request from the public showcase (SP-10) has no host notes: the
/// note of its customer for the access is <see cref="SupplierJobLocation.AccessNotes"/>, disclosed the same way.
/// </param>
/// <param name="Location">
/// The property: comune and postal code always; the name of the property and the street address only when
/// <see cref="ContactDisclosed"/> (decision D9). For a request from the public showcase (SP-10) the place is the one the
/// customer wrote on the request: comune and postal code always, street address, floor and access notes once taken.
/// </param>
/// <param name="ScheduledFor">
/// Europe/Rome calendar date of the job: the day of the scheduled time when the request has one (SP-04), otherwise the
/// check-out day of the stay for a short-rent request (the turnover); null for a long-rent request and for an older
/// short-rent request not traced to a stay (date to agree with the host).
/// </param>
/// <param name="Stay">The stay of a short-rent request (booking id and dates, no guest data); null otherwise.</param>
/// <param name="ContactDisclosed">
/// True once the supplier took the request (<see cref="SupplierJobDisclosure.IsDisclosed"/>): street address and host
/// contact are shown from then on.
/// </param>
/// <param name="HostContact">
/// The host contact, only when <see cref="ContactDisclosed"/>. For a request from the public showcase (SP-10) it is the contact of
/// the private customer who asked (name, e-mail, phone): the same block of the console, filled by the party that asked.
/// </param>
/// <param name="History">The transitions, oldest first; only in the detail (null in lists).</param>
/// <param name="Source">Where the request comes from: <see cref="SupplierRequestSources.CasaZen"/> for a host, <see cref="SupplierRequestSources.Showcase"/> for a customer of the public showcase (SP-10).</param>
/// <param name="ServiceListingId">The catalog service the request is for, when it is for one.</param>
/// <param name="ServiceName">The name of the service at the time of the request (a snapshot), when it is for a catalog service.</param>
/// <param name="Schedule">When the work is, and the deadlines of the request.</param>
/// <param name="Price">The price of the work.</param>
/// <param name="Client">
/// The customer: for a host's request, the host org (decision D9: its name is shown before the take); for a request from the
/// public showcase, the private customer, shown as "Nome C." until the supplier takes the request (decision D9).
/// </param>
/// <param name="Proposal">The time the supplier proposed and the host (or the customer) has not answered yet.</param>
/// <param name="Cancellation">When, by whom and why the request was cancelled.</param>
/// <param name="CompletionNotes">What the supplier wrote when it completed the work.</param>
/// <param name="WorkPhotos">The photos of the work, in the private bucket (the ids the download endpoint takes).</param>
/// <param name="PaymentMode">How the request is paid (SP-15a): decided when the supplier took it; <c>Manual</c> for every earlier request.</param>
/// <param name="Payment">
/// Where the payment of a completed job stands and what the supplier may do about it (SP-04e): <b>null</b> unless the request has a payment
/// record or is <c>Completato</c>. Filled by the reader (<see cref="SupplierJobPayment"/>); null in a view built without it.
/// </param>
/// <param name="AutoAccepted">The request was born taken: accepted automatically for a regular host (SP-04d); its take is a step of CasaZen itself in <paramref name="History"/>.</param>
/// <param name="Quote">The quote the supplier sent and the customer has not answered (SP-04b); <c>null</c> when there is none.</param>
public sealed record SupplierServiceRequestView(
    Guid Id,
    ServiceRequestRentalContext RentalContext,
    ServiceRequestStatus Status,
    string Category,
    ServiceRequestUrgency Urgency,
    string? Notes,
    string? RejectionReason,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? TakenAt,
    DateTime? CompletedAt,
    DateTime? PaidAt,
    SupplierJobLocation Location,
    DateOnly? ScheduledFor,
    SupplierJobStay? Stay,
    bool ContactDisclosed,
    SupplierJobHostContact? HostContact,
    IReadOnlyList<ServiceRequestHistoryEntry>? History,
    string Source,
    Guid? ServiceListingId,
    string? ServiceName,
    SupplierJobSchedule Schedule,
    SupplierJobPrice Price,
    SupplierJobClient Client,
    SupplierJobProposal? Proposal,
    SupplierJobCancellation? Cancellation,
    string? CompletionNotes,
    IReadOnlyList<ServiceRequestPhoto> WorkPhotos,
    ServiceRequestPaymentMode PaymentMode = ServiceRequestPaymentMode.Manual,
    SupplierJobPayment? Payment = null,
    bool AutoAccepted = false,
    ServiceRequestKind Kind = ServiceRequestKind.Standard,
    ServiceRequestQuote? Quote = null)
{
    /// <summary>
    /// The phase of the request (SP-04e): <see cref="SupplierRequestPhases"/>, derived from the status, the proposed time and the quote in
    /// one place. An open set: a client shows a generic wording for a value it does not know.
    /// </summary>
    public string Phase => SupplierRequestPhases.Of(Status, hasPendingProposal: Proposal is not null, hasPendingQuote: Quote is not null);
}

/// <summary>
/// The payment of a completed job as the supplier sees it (SP-04e): the split "price, commission, net" of the payment record when
/// there is one, and what the supplier may do now, computed with <b>the rules of the endpoints</b> it would call
/// (<see cref="ServicePaymentActions"/>), so a button of the console and the answer of the server agree.
/// </summary>
/// <remarks>
/// <para>It exists when the request has a payment record or is <c>Completato</c>. A completed job paid outside CasaZen has no record
/// yet: <see cref="Id"/> and <see cref="Status"/> are then null, <see cref="AmountCents"/> is the final amount the supplier declared
/// and the commission fields are null (nothing was computed), but the two actions are still told. A job the host marked paid by
/// hand has no record and no summary.</para>
/// <para><see cref="Status"/> is the status of the record (<see cref="ServicePaymentStatus"/>) and <b>an open set</b>: the held
/// payments of SP-15c will add values.</para>
/// </remarks>
/// <param name="Id">The payment record; null while the job has none.</param>
/// <param name="Status">Where the record is; null while there is none.</param>
/// <param name="AmountCents">What the payer pays, gross, in cents: the record's amount, else the declared final amount.</param>
/// <param name="CommissionPercent">The commission percentage the record was created with (a snapshot); null while there is no record.</param>
/// <param name="CommissionCents">The commission CasaZen keeps, in cents (0 when none is sent); null while there is no record.</param>
/// <param name="NetCents">What the supplier receives before Stripe's own fees, in cents; null while there is no record.</param>
/// <param name="PaidAt">When it was paid.</param>
/// <param name="PaidVia">How it was collected (<c>Stripe</c> or <c>Offline</c>).</param>
/// <param name="RequestedAt">When the payer was first asked; null while nothing was sent.</param>
/// <param name="LastSentAt">When the payer was last sent a link.</param>
/// <param name="SentCount">Emails with a link sent so far.</param>
/// <param name="Late">The payer is late: the record was flagged late and is still owed.</param>
/// <param name="NeedsAmountConfirmation">The final amount is above the quote by more than the tolerance and the host has not confirmed it (decision D7).</param>
/// <param name="CanRequest">The supplier may ask the payer for the payment (or remind them) now: <c>POST api/supplier/requests/{id}/payment-request</c> would not refuse.</param>
/// <param name="CanRecordOffline">The supplier may record that it was paid outside CasaZen now: <c>POST api/supplier/requests/{id}/payment/offline</c> would not refuse.</param>
public sealed record SupplierJobPayment(
    Guid? Id,
    ServicePaymentStatus? Status,
    int? AmountCents,
    decimal? CommissionPercent,
    int? CommissionCents,
    int? NetCents,
    DateTime? PaidAt,
    ServicePaymentChannel? PaidVia,
    DateTime? RequestedAt,
    DateTime? LastSentAt,
    int SentCount,
    bool Late,
    bool NeedsAmountConfirmation,
    bool CanRequest,
    bool CanRecordOffline);

/// <summary>The values of <see cref="SupplierServiceRequestView.Source"/>.</summary>
public static class SupplierRequestSources
{
    /// <summary>A request of a CasaZen host (short-rent or long-rent).</summary>
    public const string CasaZen = "casazen";

    /// <summary>A booking from the supplier's public showcase (SP-10): a customer with no account, e-mail checked.</summary>
    public const string Showcase = "showcase";

    /// <summary>The value of <see cref="SupplierServiceRequestView.Source"/> for a request of <paramref name="source"/>.</summary>
    public static string Of(Entities.Enums.ServiceRequestSource source) =>
        source == Entities.Enums.ServiceRequestSource.Showcase ? Showcase : CasaZen;

    /// <summary>
    /// The origin named <paramref name="value"/> (<c>casazen</c> or <c>showcase</c>, any case): the <c>source</c> filter of the inbox
    /// (SP-04e). False for any other value, including numbers and the enum names.
    /// </summary>
    public static bool TryParse(string? value, out Entities.Enums.ServiceRequestSource source)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case CasaZen:
                source = Entities.Enums.ServiceRequestSource.Host;
                return true;
            case Showcase:
                source = Entities.Enums.ServiceRequestSource.Showcase;
                return true;
            default:
                source = default;
                return false;
        }
    }
}

/// <summary>
/// Where the job is. <paramref name="PropertyName"/>, <paramref name="Address"/>, <paramref name="Floor"/> and
/// <paramref name="AccessNotes"/> are null until the supplier takes the request.
/// </summary>
/// <param name="PropertyId">The host's property; <c>null</c> for a request from the public showcase (it has no property).</param>
/// <param name="City">The comune of the property, as the host wrote it (for a showcase request, as the customer wrote it).</param>
/// <param name="PostalCode">The postal code (the zone within the comune), null when the host left it empty.</param>
/// <param name="Floor">Floor and apartment, for a request from the public showcase.</param>
/// <param name="AccessNotes">The customer's note for the access (doorbell, keys), for a request from the public showcase.</param>
public sealed record SupplierJobLocation(
    Guid? PropertyId,
    string? PropertyName,
    string City,
    string? PostalCode,
    string? Address,
    string? Floor = null,
    string? AccessNotes = null);

/// <summary>The stay a short-rent request is for: its id and its Europe/Rome dates, never the guest.</summary>
public sealed record SupplierJobStay(Guid BookingId, DateOnly CheckIn, DateOnly CheckOut);

/// <summary>
/// The host contact given to the supplier after the take: the org's display name and contact email (the same contact
/// the guests get), and the phone of the property owner's profile when the owner filled it in.
/// </summary>
public sealed record SupplierJobHostContact(string Name, string? Email, string? Phone);

/// <summary>
/// When the work is: the scheduled interval (UTC; both null for a request still "da concordare"), the instant the supplier
/// has to answer by (only while the request is new), and when the work started.
/// </summary>
public sealed record SupplierJobSchedule(DateTime? StartUtc, DateTime? EndUtc, DateTime? RespondBy, DateTime? StartedAt);

/// <summary>
/// The price of a request in cents of euro: what was estimated, what the supplier quoted, the final total with the lines
/// it adds up to, and whether the customer has to confirm it (decision D7).
/// </summary>
public sealed record SupplierJobPrice(
    int? EstimatedAmountCents,
    int? QuotedAmountCents,
    int? FinalAmountCents,
    bool FinalAmountNeedsConfirmation,
    IReadOnlyList<ServiceRequestPriceLine> Lines)
{
    /// <summary>The amount to show: the final total, else the quote, else the estimate; <c>null</c> is "da concordare".</summary>
    public int? AmountCents => FinalAmountCents ?? QuotedAmountCents ?? EstimatedAmountCents;
}

/// <summary>
/// The customer of a request. For a host it is the org the request belongs to: <paramref name="Id"/> filters the inbox
/// (<c>clientId</c>) and the name is the org's display name, which decision D9 shows before the take (the property, the
/// address and the contact are not). For a private customer of the public showcase (SP-10) it is the
/// <c>ServiceCustomer</c>: its id filters the inbox too, and the name is <c>Nome C.</c> until the request is taken
/// (<see cref="SupplierJobDisclosure.CustomerName"/>).
/// </summary>
public sealed record SupplierJobClient(Guid Id, string Name);

/// <summary>A time the supplier proposed instead of the requested one, waiting for the host's answer.</summary>
public sealed record SupplierJobProposal(DateTime StartUtc, DateTime EndUtc, DateTime ProposedAt, string? Message);

/// <summary>
/// A cancelled request: when, by whom, and the reason (the text written, or one of the codes of
/// <see cref="ServiceRequestCancellationReasons"/>: <c>NoResponse</c>, <c>ProposalNotAnswered</c>, <c>CancelledByCustomer</c>).
/// </summary>
public sealed record SupplierJobCancellation(DateTime At, ServiceRequestActorParty By, string? Reason);

/// <summary>One step of the history of a service request.</summary>
/// <param name="Status">The status the request reached.</param>
/// <param name="At">UTC instant of the transition.</param>
/// <param name="Actor">The party that made it.</param>
/// <param name="ActorName">
/// The member of the supplier org who made it, when known (the take records the user); null otherwise. Host members are
/// never named: the host is identified by the host contact.
/// </param>
/// <param name="Reason">The reason of a <see cref="ServiceRequestStatus.Rifiutato"/> or <see cref="ServiceRequestStatus.Annullato"/> step.</param>
/// <param name="Event">
/// What the step says beyond the status (SP-04b), one of <see cref="ServiceRequestHistoryEvents"/>: the steps about a quote of the supplier
/// (sent, rejected, withdrawn) are of the status <c>Richiesto</c>, and the take that accepted a quote and the cancellation that came from a quote
/// nobody answered carry it too. <c>null</c> for the plain steps of the state machine. <b>An open set.</b>
/// </param>
public sealed record ServiceRequestHistoryEntry(
    ServiceRequestStatus Status,
    DateTime At,
    ServiceRequestActorParty Actor,
    string? ActorName,
    string? Reason,
    string? Event = null);

/// <summary>The values of <see cref="ServiceRequestHistoryEntry.Event"/>: the quote steps of a request (SP-04b).</summary>
public static class ServiceRequestHistoryEvents
{
    /// <summary>The supplier sent a quote (or replaced the one that waited).</summary>
    public const string QuoteSent = "quote_sent";

    /// <summary>The take of the request is the acceptance of a quote: its total is the agreed price.</summary>
    public const string QuoteAccepted = "quote_accepted";

    /// <summary>The customer turned a quote down: the request is "to answer" again.</summary>
    public const string QuoteRejected = "quote_rejected";

    /// <summary>The supplier took its quote back: the request is "to answer" again.</summary>
    public const string QuoteWithdrawn = "quote_withdrawn";

    /// <summary>The cancellation of a request whose customer did not answer the quote in time.</summary>
    public const string QuoteExpired = "quote_expired";
}

/// <summary>
/// What the supplier sees before and after taking a request (SU-08, GDPR data minimization; decision D9): before the take the
/// comune, the zone (postal code), the day and time, the price and who asks (the host's name), enough to accept or refuse;
/// from the take on also the name of the property, the host's notes, the street address and the host contact, needed to do
/// the job. A request the supplier rejected, or that was cancelled, shows none of them: the first was never taken, and
/// the second has no job to do anymore.
/// </summary>
/// <remarks>
/// <b>A private customer of the public showcase (SP-10)</b> follows the same line: before the take the supplier knows the comune,
/// the postal code, the day and time, the price and the name as <c>Nome C.</c> (first name and the initial of the last one,
/// <see cref="CustomerName"/>); from the take on, the full name, the e-mail, the phone, the street address, the floor and the
/// access notes.
/// </remarks>
public static class SupplierJobDisclosure
{
    /// <summary>True when the property, the notes, the street address and the host contact are shown for a request in <paramref name="status"/>.</summary>
    public static bool IsDisclosed(ServiceRequestStatus status) => status is
        ServiceRequestStatus.PresoInCarico
        or ServiceRequestStatus.InCorso
        or ServiceRequestStatus.Completato
        or ServiceRequestStatus.Pagato;

    /// <summary>
    /// The name of a private customer as the supplier sees it for a request in <paramref name="status"/> (decision D9): the full
    /// name once the request is taken, <c>Nome C.</c> before and for a request that was rejected or cancelled
    /// (<see cref="ShowcaseBookingRules.AbbreviateName"/>).
    /// </summary>
    public static string CustomerName(ServiceRequestStatus status, string? fullName) =>
        IsDisclosed(status) ? (fullName ?? string.Empty).Trim() : ShowcaseBookingRules.AbbreviateName(fullName);
}
