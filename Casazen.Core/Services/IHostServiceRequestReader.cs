using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>The stay a request is for, as its host reads it (SR-08a). The supplier never gets this: the guest is the host's.</summary>
public sealed record HostRequestStay(Guid BookingId, string GuestName, DateOnly CheckIn, DateOnly CheckOut);

/// <summary>How to reach the supplier of a request, from the moment it took it (SR-08a). Never before: until then the supplier owes the host no contact.</summary>
public sealed record HostRequestSupplierContact(string? Phone, string? Email);

/// <summary>What a request says to its host besides the request itself: the city, the stay, the contact, and what the host can do.</summary>
public sealed record HostServiceRequestExtras(
    string? PropertyCity,
    HostRequestStay? Stay,
    HostRequestSupplierContact? SupplierContact,
    HostServiceRequestAction Action);

/// <summary>A page of the host's requests, each with its <see cref="HostServiceRequestExtras"/> (by request id).</summary>
public sealed record HostServiceRequestPage(
    IReadOnlyList<ServiceRequest> Items,
    IReadOnlyDictionary<Guid, HostServiceRequestExtras> Extras,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// What the host reads of its short-rent requests (SR-08a): the filterable list with the counters of its chips, and the extras of a
/// request (the city, the stay, the supplier's contact, the action the host can do) for the list, the detail and the answers of the
/// host's own actions. Reads only: the changes are <see cref="IServiceRequestService"/>'s.
/// </summary>
/// <remarks>
/// <b>Tenancy.</b> <c>ServiceRequest</c> has two parties and no tenant filter, so every statement here carries the host org of the
/// <see cref="HostScope"/> as an explicit predicate and, for a restricted scope, <c>InScope</c>. A request of a supplier's showcase
/// or of the long-rent context is never part of it. The cost is constant: one statement for the page, one for the total only when the
/// page is full, one for the stays and one for the supplier's contacts, whatever the size of the page.
/// </remarks>
public interface IHostServiceRequestReader
{
    /// <summary>The requests that pass <paramref name="query"/>, the page asked for.</summary>
    Task<HostServiceRequestPage> ListAsync(HostScope scope, HostServiceRequestQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many requests are in each chip, for those that pass the filters of <paramref name="query"/> but the chip (<c>View</c>), the
    /// page and the order. One aggregate statement.
    /// </summary>
    Task<HostServiceRequestCounts> CountAsync(HostScope scope, HostServiceRequestQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The extras of requests the caller already read (the detail, the answer of an action of the host). Requests of another org are
    /// left out: the host org is a predicate of the reads, never trusted from the requests.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, HostServiceRequestExtras>> DescribeAsync(
        Guid hostOrgId,
        IReadOnlyCollection<ServiceRequest> requests,
        CancellationToken cancellationToken = default);
}
