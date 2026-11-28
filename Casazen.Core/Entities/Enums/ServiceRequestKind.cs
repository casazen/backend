namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a service request asks for (SP-10b). Stored as an integer in <c>ServiceRequests.Kind</c>: never reorder or renumber, only
/// append. It is <b>not</b> a status: both kinds share the lifecycle of <see cref="ServiceRequestStatus"/>, which is why the mobile
/// app, the inbox, the counters and the cancellations keep working without a new status value.
/// </summary>
public enum ServiceRequestKind
{
    Standard = 0,

    Custom = 1,
}

public enum ServiceRequestMessageAuthor
{
    Customer = 0,

    Supplier = 1,

    System = 2,
}
