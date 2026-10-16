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

    /// <summary>
    /// How many requests of <paramref name="supplierOrgId"/> each tab of the inbox holds, and how many payments are late (SP-04e,
    /// <c>GET api/supplier/inbox/counts</c>): the numbers on the chips of the list and on the menu, computed in one place so they
    /// agree with the lists. Read-only, scoped by the supplier org.
    /// </summary>
    Task<SupplierInboxCounts> GetInboxCountsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The counters of the tabs of the supplier's inbox (SP-04e). The tabs <c>da-rispondere</c> and <c>in-attesa</c> split <c>nuove</c>:
/// their sum is what <c>nuove</c> holds.
/// </summary>
/// <param name="ToAnswer">Requests waiting for the supplier's answer, with no proposed time (nor quote) waiting (tab <c>da-rispondere</c>).</param>
/// <param name="AwaitingCustomer">Requests the supplier answered with a proposed time (or a quote) the customer has not answered (tab <c>in-attesa</c>).</param>
/// <param name="Scheduled">Requests taken or in progress (tab <c>programmate</c>).</param>
/// <param name="ToCollect">Requests completed and not paid yet (tab <c>da-incassare</c>).</param>
/// <param name="Archive">Requests that are over for the supplier: paid, rejected or cancelled (tab <c>archivio</c>).</param>
/// <param name="PaymentsLate">Payments asked for that are late and still owed.</param>
public sealed record SupplierInboxCounts(int ToAnswer, int AwaitingCustomer, int Scheduled, int ToCollect, int Archive, int PaymentsLate);
