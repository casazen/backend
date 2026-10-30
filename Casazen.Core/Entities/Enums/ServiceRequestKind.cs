namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a service request asks for (SP-10b). Stored as an integer in <c>ServiceRequests.Kind</c>: never reorder or renumber, only
/// append. It is <b>not</b> a status: both kinds share the lifecycle of <see cref="ServiceRequestStatus"/>, which is why the mobile
/// app, the inbox, the counters and the cancellations keep working without a new status value.
/// </summary>
public enum ServiceRequestKind
{
    /// <summary>
    /// What every request was before SP-10b: a host's request, or the booking of a published service at a free slot of a supplier's
    /// showcase.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// A request on measure from a supplier's public showcase (SP-10b): the customer describes what it needs in its own words, with
    /// no slot and no price, and talks with the supplier in the thread of the request (<see cref="ServiceRequestMessage"/>). It is
    /// born <see cref="ServiceRequestStatus.Richiesto"/> without a time. Only the showcase context has them
    /// (<c>CK_ServiceRequests_Kind</c>).
    /// </summary>
    Custom = 1,
}

/// <summary>Who wrote a message of the thread of a request on measure (<see cref="ServiceRequestMessage.Author"/>). Never renumber.</summary>
public enum ServiceRequestMessageAuthor
{
    /// <summary>The customer of the showcase request.</summary>
    Customer = 0,

    /// <summary>A member of the supplier.</summary>
    Supplier = 1,

    /// <summary>CasaZen itself (reserved: no flow writes one yet).</summary>
    System = 2,
}
