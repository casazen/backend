using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The supplier console's reads of the service requests (SU-08, A4-14; SP-04): inbox and history pages, and the detail of one
/// request, as <see cref="SupplierServiceRequestView"/> (property, notes, address and host contact only after the take,
/// never guest data; decision D9). Every read is scoped by the caller's supplier org: another supplier's request is not found.
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

    /// <summary>
    /// The requests of <paramref name="supplierOrgId"/> that have a day of their own between <paramref name="from"/> and
    /// <paramref name="to"/> (Europe/Rome days, both included), as the items of the supplier's agenda (SP-03): every status
    /// except <c>Rifiutato</c> and <c>Annullato</c>, by day. A request has a day while it has a time of its own (SP-04: the
    /// day of its scheduled start, with its hours) or is a short-rent request tied to a stay (the check-out day, without
    /// hours); the ones with neither (long-rent, or older, "da concordare") are not in the agenda. Only the fields of
    /// <see cref="SupplierAgendaRequest"/>: no property, address or contact.
    /// </summary>
    Task<IReadOnlyList<SupplierAgendaRequest>> ListForAgendaAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}
