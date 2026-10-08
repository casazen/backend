namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who moved a service request to a status. Used by the history of a request and, since SP-04, stored as the author of a
/// cancellation (<c>ServiceRequests.CancelledBy</c>): the values are stored, never reorder or renumber, only append.
/// </summary>
public enum ServiceRequestActorParty
{
    /// <summary>The host org (creation, cancellation, payment).</summary>
    Host = 0,

    /// <summary>The supplier org (take, start, completion, rejection, cancellation before the start).</summary>
    Supplier = 1,

    /// <summary>CasaZen itself: the automatic cancellation of a request nobody answered in time (decision D8).</summary>
    System = 2,
}
