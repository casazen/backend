using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The supplier console's reads of the service requests (SU-08, A4-14): inbox and history pages, and the detail of one
/// request, as <see cref="SupplierServiceRequestView"/> (address and host contact only after the take, never guest
/// data). Every read is scoped by the caller's supplier org: another supplier's request is not found.
/// </summary>
public interface ISupplierServiceRequestReader
{
    /// <summary>The requests of <paramref name="supplierOrgId"/> matching <paramref name="query"/>, and their total.</summary>
    Task<(IReadOnlyList<SupplierServiceRequestView> Items, int Total)> ListAsync(
        Guid supplierOrgId,
        SupplierInboxQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The request with its history when it was sent to <paramref name="supplierOrgId"/>; null otherwise (missing, or
    /// sent to another supplier).
    /// </summary>
    Task<SupplierServiceRequestView?> GetAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);
}
