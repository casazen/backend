using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

public sealed record HostRequestStay(Guid BookingId, string GuestName, DateOnly CheckIn, DateOnly CheckOut);

public sealed record HostRequestSupplierContact(string? Phone, string? Email);

public sealed record HostServiceRequestExtras(
    string? PropertyCity,
    HostRequestStay? Stay,
    HostRequestSupplierContact? SupplierContact,
    HostServiceRequestAction Action);

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
    Task<HostServiceRequestPage> ListAsync(HostScope scope, HostServiceRequestQuery query, CancellationToken cancellationToken = default);

    Task<HostServiceRequestCounts> CountAsync(HostScope scope, HostServiceRequestQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, HostServiceRequestExtras>> DescribeAsync(
        Guid hostOrgId,
        IReadOnlyCollection<ServiceRequest> requests,
        CancellationToken cancellationToken = default);
}
