using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// A service request as the supplier it was sent to sees it (SU-08, A4-14; SP-04): where and when the job is, the price,
/// and the host contact once the supplier took it. Built only from the fields listed here, so no guest data (name, email,
/// document, phone) can ever reach the supplier: the stay is given by its dates only.
/// </summary>
/// <param name="Notes">
/// The host's notes for the supplier: <b>null until the supplier takes the request</b> (decision D9, they may name a person or
/// a door code), like the street address and the contact.
/// </param>
/// <param name="Location">
/// The property: comune and postal code always; the name of the property and the street address only when
/// <see cref="ContactDisclosed"/> (decision D9).
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
/// <param name="HostContact">The host contact, only when <see cref="ContactDisclosed"/>.</param>
/// <param name="History">The transitions, oldest first; only in the detail (null in lists).</param>
/// <param name="Source">Where the request comes from: <see cref="SupplierRequestSources.CasaZen"/> for a host (the showcase booking arrives with SP-10).</param>
/// <param name="ServiceListingId">The catalog service the request is for, when it is for one.</param>
/// <param name="ServiceName">The name of the service at the time of the request (a snapshot), when it is for a catalog service.</param>
/// <param name="Schedule">When the work is, and the deadlines of the request.</param>
/// <param name="Price">The price of the work.</param>
/// <param name="Client">The customer: for a host's request, the host org (decision D9: its name is shown before the take).</param>
/// <param name="Proposal">The time the supplier proposed and the host has not answered yet.</param>
/// <param name="Cancellation">When, by whom and why the request was cancelled.</param>
/// <param name="CompletionNotes">What the supplier wrote when it completed the work.</param>
/// <param name="WorkPhotos">The photos of the work, in the private bucket (the ids the download endpoint takes).</param>
/// <param name="PaymentMode">How the request is paid (SP-15a): decided when the supplier took it; <c>Manual</c> for every earlier request.</param>
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
    ServiceRequestPaymentMode PaymentMode = ServiceRequestPaymentMode.Manual);

/// <summary>The values of <see cref="SupplierServiceRequestView.Source"/>.</summary>
public static class SupplierRequestSources
{
    /// <summary>A request of a CasaZen host (short-rent or long-rent).</summary>
    public const string CasaZen = "casazen";

    /// <summary>A booking from the supplier's public showcase (SP-10): not produced yet.</summary>
    public const string Showcase = "showcase";
}

/// <summary>Where the job is. <paramref name="PropertyName"/> and <paramref name="Address"/> are null until the supplier takes the request.</summary>
/// <param name="City">The comune of the property, as the host wrote it.</param>
/// <param name="PostalCode">The postal code (the zone within the comune), null when the host left it empty.</param>
public sealed record SupplierJobLocation(
    Guid PropertyId,
    string? PropertyName,
    string City,
    string? PostalCode,
    string? Address);

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
/// address and the contact are not).
/// </summary>
public sealed record SupplierJobClient(Guid Id, string Name);

/// <summary>A time the supplier proposed instead of the requested one, waiting for the host's answer.</summary>
public sealed record SupplierJobProposal(DateTime StartUtc, DateTime EndUtc, DateTime ProposedAt, string? Message);

/// <summary>A cancelled request: when, by whom, and the reason (the text written, or <c>NoResponse</c>).</summary>
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
public sealed record ServiceRequestHistoryEntry(
    ServiceRequestStatus Status,
    DateTime At,
    ServiceRequestActorParty Actor,
    string? ActorName,
    string? Reason);

/// <summary>
/// What the supplier sees before and after taking a request (SU-08, GDPR data minimization; decision D9): before the take the
/// comune, the zone (postal code), the day and time, the price and who asks (the host's name), enough to accept or refuse;
/// from the take on also the name of the property, the host's notes, the street address and the host contact, needed to do
/// the job. A request the supplier rejected, or that was cancelled, shows none of them: the first was never taken, and
/// the second has no job to do anymore.
/// </summary>
public static class SupplierJobDisclosure
{
    /// <summary>True when the property, the notes, the street address and the host contact are shown for a request in <paramref name="status"/>.</summary>
    public static bool IsDisclosed(ServiceRequestStatus status) => status is
        ServiceRequestStatus.PresoInCarico
        or ServiceRequestStatus.InCorso
        or ServiceRequestStatus.Completato
        or ServiceRequestStatus.Pagato;
}
