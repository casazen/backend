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

    public async Task<SupplierEarningsSummary> GetEarningsSummaryAsync(
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = SupplierKpiPeriods.Resolve(SupplierKpiPeriod.CurrentMonth, timeProvider.TodayInRomeAsDateOnly());
        // The month is made of Europe/Rome calendar days up to its last one: [00:00 Rome of the 1st, 00:00 Rome of the 1st of the next month).
        var monthEnd = new DateOnly(from.Year, from.Month, 1).AddMonths(1).AddDays(-1);
        var startUtc = RomeCalendar.StartOfDayUtc(from);
        var endUtc = RomeCalendar.StartOfDayUtc(monthEnd.AddDays(1));

        // A paid request was completed first: it counts on its completion date, like the completed KPI. The amounts are the
        // final amounts the supplier declared; a job completed without one counts as a job and adds nothing.
        var month = await CompletedInOf(db, supplierOrgId, startUtc, endUtc).SingleOrDefaultAsync(cancellationToken);
        var toCollect = await ToCollectOf(db, supplierOrgId).SingleOrDefaultAsync(cancellationToken);

        // Read as pairs and averaged here, so the arithmetic on instants does not depend on the provider.
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-SupplierEarningsSummary.ResponseWindowDays);
        var answers = await AnswersOf(db, supplierOrgId, since).ToListAsync(cancellationToken);
        int? averageMinutes = answers.Count == 0
            ? null
            : (int)Math.Round(answers.Average(answer => (answer.TakenAt - answer.CreatedAt).TotalMinutes));

        return new SupplierEarningsSummary(
            from,
            monthEnd,
            MonthAmountCents: month?.Amount ?? 0,
            MonthJobs: month?.Jobs ?? 0,
            ToCollectAmountCents: toCollect?.Amount ?? 0,
            ToCollectJobs: toCollect?.Jobs ?? 0,
            AverageResponseMinutes: averageMinutes,
            IsEstimate: true);
    }

    public Task<bool> HasAnsweredARequestAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId)
            .AnyAsync(r => r.TakenAt != null || r.Status == ServiceRequestStatus.Rifiutato, cancellationToken);

    /// <summary>The jobs and the final amounts of the requests of the supplier completed (or paid) inside <c>[startUtc, endUtc)</c>.</summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<AmountGroup> CompletedInOf(AppDbContext db, Guid supplierOrgId, DateTime startUtc, DateTime endUtc) =>
        // ServiceRequest has no tenant filter (two parties, TN-2 allow-list): scoped by the supplier org explicitly.
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato)
                        && r.CompletedAt >= startUtc
                        && r.CompletedAt < endUtc)
            .GroupBy(_ => 1)
            .Select(g => new AmountGroup(g.Count(), g.Sum(r => (long?)r.FinalAmountCents)));

    /// <summary>The completed requests of the supplier not yet marked as paid, with their final amounts.</summary>
    internal static IQueryable<AmountGroup> ToCollectOf(AppDbContext db, Guid supplierOrgId) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && r.Status == ServiceRequestStatus.Completato)
            .GroupBy(_ => 1)
            .Select(g => new AmountGroup(g.Count(), g.Sum(r => (long?)r.FinalAmountCents)));

    /// <summary>When each request taken since <paramref name="sinceUtc"/> was received and taken, for the average response time.</summary>
    internal static IQueryable<AnswerPair> AnswersOf(AppDbContext db, Guid supplierOrgId, DateTime sinceUtc) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && r.TakenAt != null && r.TakenAt >= sinceUtc)
            .Select(r => new AnswerPair(r.CreatedAt, r.TakenAt!.Value));

    /// <summary>A number of requests and the sum of their final amounts (<c>null</c> when none has one).</summary>
    internal sealed record AmountGroup(int Jobs, long? Amount);

    /// <summary>The moment a request was received and the moment the supplier took it.</summary>
    internal sealed record AnswerPair(DateTime CreatedAt, DateTime TakenAt);
}
