using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Supplier dashboard KPIs from <c>ServiceRequests</c> (SU-11, A4-15). The dashboard used to count the supplier jobs,
/// which no real flow created, so a supplier with five completed requests read "0 completed".
/// </summary>
public class SupplierKpiService(AppDbContext db, TimeProvider timeProvider) : ISupplierKpiService
{
    public async Task<SupplierServiceRequestKpis> GetKpisAsync(
        Guid supplierOrgId,
        SupplierKpiPeriod period,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = SupplierKpiPeriods.Resolve(period, timeProvider.TodayInRomeAsDateOnly());

        // The period is made of Europe/Rome calendar days: [00:00 Rome of From, 00:00 Rome of the day after To).
        var startUtc = RomeCalendar.StartOfDayUtc(from.ToDateTime(TimeOnly.MinValue));
        var endUtc = RomeCalendar.StartOfDayUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue));

        // ServiceRequest has no tenant filter (two parties, TN-2 allow-list): scoped by the supplier org explicitly, so
        // another supplier's requests and the host orgs' other requests are never counted.
        var counts = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                AwaitingAcceptance = g.Count(r => r.Status == ServiceRequestStatus.Richiesto),
                Upcoming = g.Count(r =>
                    r.Status == ServiceRequestStatus.PresoInCarico || r.Status == ServiceRequestStatus.InCorso),
                // A paid request was completed first: it counts on its completion date.
                Completed = g.Count(r =>
                    (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato)
                    && r.CompletedAt >= startUtc && r.CompletedAt < endUtc),
                // Rifiutato is a final state (ServiceRequestStateMachine): its last update is the rejection.
                Rejected = g.Count(r =>
                    r.Status == ServiceRequestStatus.Rifiutato && r.UpdatedAt >= startUtc && r.UpdatedAt < endUtc),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return new SupplierServiceRequestKpis(
            period,
            from,
            to,
            Completed: counts?.Completed ?? 0,
            Rejected: counts?.Rejected ?? 0,
            AwaitingAcceptance: counts?.AwaitingAcceptance ?? 0,
            Upcoming: counts?.Upcoming ?? 0,
            TotalRequests: counts?.Total ?? 0);
    }
}
