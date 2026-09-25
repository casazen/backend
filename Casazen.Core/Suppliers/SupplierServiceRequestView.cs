using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// A service request as the supplier it was sent to sees it (SU-08, A4-14): where and when the job is, and the host
/// contact once the supplier took it. Built only from the fields listed here, so no guest data (name, email, document,
/// phone) can ever reach the supplier: the stay is given by its dates only.
/// </summary>
/// <param name="Location">The property: comune always, street address only when <see cref="ContactDisclosed"/>.</param>
/// <param name="ScheduledFor">
/// Europe/Rome calendar date of the job: the check-out day of the stay for a short-rent request (the turnover); null for
/// a long-rent request and for an older short-rent request not traced to a stay (date to agree with the host).
/// </param>
/// <param name="Stay">The stay of a short-rent request (booking id and dates, no guest data); null otherwise.</param>
/// <param name="ContactDisclosed">
/// True once the supplier took the request (<see cref="SupplierJobDisclosure.IsDisclosed"/>): street address and host
/// contact are shown from then on.
/// </param>
/// <param name="HostContact">The host contact, only when <see cref="ContactDisclosed"/>.</param>
/// <param name="History">The transitions, oldest first; only in the detail (null in lists).</param>
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
    IReadOnlyList<ServiceRequestHistoryEntry>? History);

/// <summary>Where the job is. <paramref name="Address"/> is null until the supplier takes the request.</summary>
/// <param name="City">The comune of the property, as the host wrote it.</param>
/// <param name="PostalCode">The postal code (the zone within the comune), null when the host left it empty.</param>
public sealed record SupplierJobLocation(
    Guid PropertyId,
    string PropertyName,
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

/// <summary>Who moved a service request to a status.</summary>
public enum ServiceRequestActorParty
{
    /// <summary>The host org (creation, payment).</summary>
    Host,

    /// <summary>The supplier org (take, completion, rejection).</summary>
    Supplier,
}

/// <summary>One step of the history of a service request.</summary>
/// <param name="Status">The status the request reached.</param>
/// <param name="At">UTC instant of the transition.</param>
/// <param name="Actor">The party that made it.</param>
/// <param name="ActorName">
/// The member of the supplier org who made it, when known (the take records the user); null otherwise. Host members are
/// never named: the host is identified by the host contact.
/// </param>
/// <param name="Reason">The rejection reason of a <see cref="ServiceRequestStatus.Rifiutato"/> step.</param>
public sealed record ServiceRequestHistoryEntry(
    ServiceRequestStatus Status,
    DateTime At,
    ServiceRequestActorParty Actor,
    string? ActorName,
    string? Reason);

/// <summary>
/// What the supplier sees before and after taking a request (SU-08, GDPR data minimization): before the take the comune,
/// the zone (postal code), the date and the stay dates, enough to accept or refuse; from the take on also the street
/// address and the host contact, needed to do the job. A request the supplier rejected never shows them (it was rejected
/// before being taken).
/// </summary>
public static class SupplierJobDisclosure
{
    /// <summary>True when the street address and the host contact are shown for a request in <paramref name="status"/>.</summary>
    public static bool IsDisclosed(ServiceRequestStatus status) => status is
        ServiceRequestStatus.PresoInCarico
        or ServiceRequestStatus.InCorso
        or ServiceRequestStatus.Completato
        or ServiceRequestStatus.Pagato;
}
