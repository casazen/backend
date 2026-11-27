using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public partial class SupplierServiceRequestReader
{
    public async Task<SupplierInboxCounts> GetInboxCountsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var counts = await TabCountsOf(db, supplierOrgId).SingleOrDefaultAsync(cancellationToken);
        var late = payments is null ? 0 : await payments.CountLateAsync(supplierOrgId, cancellationToken);

        return new SupplierInboxCounts(
            counts?.ToAnswer ?? 0,
            counts?.AwaitingCustomer ?? 0,
            counts?.Scheduled ?? 0,
            counts?.ToCollect ?? 0,
            counts?.Archive ?? 0,
            late);
    }

    /// <summary>
    /// The size of each tab, counted by the database in one pass over the requests of the supplier: the same statuses and the same
    /// "has a proposal" test the tabs of <see cref="Filter"/> use (<see cref="SupplierInboxTabs"/>), so a number and a list cannot disagree.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<TabCounts> TabCountsOf(AppDbContext db, Guid supplierOrgId) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId)
            .GroupBy(_ => 1)
            .Select(g => new TabCounts(
                g.Count(r => r.Status == ServiceRequestStatus.Richiesto && r.ProposedStartUtc == null && r.QuoteSentAt == null),
                g.Count(r => r.Status == ServiceRequestStatus.Richiesto && (r.ProposedStartUtc != null || r.QuoteSentAt != null)),
                g.Count(r => r.Status == ServiceRequestStatus.PresoInCarico || r.Status == ServiceRequestStatus.InCorso),
                g.Count(r => r.Status == ServiceRequestStatus.Completato),
                g.Count(r => r.Status == ServiceRequestStatus.Pagato
                             || r.Status == ServiceRequestStatus.Rifiutato
                             || r.Status == ServiceRequestStatus.Annullato)));

    private async Task<IReadOnlyDictionary<Guid, SupplierJobPayment>> PaymentSummariesAsync(
        Guid supplierOrgId,
        IReadOnlyCollection<SupplierRequestRow> rows,
        CancellationToken cancellationToken)
    {
        if (payments is null)
            return new Dictionary<Guid, SupplierJobPayment>();

        var inputs = rows
            .Where(r => r.Status is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato)
            .Select(r => new SupplierPaymentSummaryInput(
                r.Id,
                r.Status,
                r.PaymentMode,
                r.FinalAmountCents,
                r.FinalAmountNeedsConfirmation,
                r.RentalContext != ServiceRequestRentalContext.Showcase && !string.IsNullOrWhiteSpace(r.HostEmail)))
            .ToList();

        return inputs.Count == 0
            ? new Dictionary<Guid, SupplierJobPayment>()
            : await payments.SummariesAsync(supplierOrgId, inputs, cancellationToken);
    }

    internal sealed record TabCounts(int ToAnswer, int AwaitingCustomer, int Scheduled, int ToCollect, int Archive);
}
