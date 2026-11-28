using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Supplier dashboard KPIs from <c>ServiceRequests</c> (SU-11, A4-15). The dashboard used to count the supplier jobs,
/// which no real flow created, so a supplier with five completed requests read "0 completed". The overview of the home (SP-04e)
/// is in <c>SupplierKpiService.Overview.cs</c>; the payments it adds up come from <see cref="ISupplierPaymentReadModels"/>.
/// </summary>
/// <param name="db">The context.</param>
/// <param name="timeProvider">The clock: "today" and the periods are Europe/Rome days.</param>
/// <param name="features">The feature flags; without them the overview talks about no online payments.</param>
/// <param name="payments">The reads of the payments; without them the overview talks about no online payments.</param>
public partial class SupplierKpiService(
    AppDbContext db,
    TimeProvider timeProvider,
    IFeatureFlags? features = null,
    ISupplierPaymentReadModels? payments = null) : ISupplierKpiService
{
    public async Task<SupplierServiceRequestKpis> GetKpisAsync(
        Guid supplierOrgId,
        SupplierKpiPeriod period,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = SupplierKpiPeriods.Resolve(period, timeProvider.TodayInRomeAsDateOnly());

        var startUtc = RomeCalendar.StartOfDayUtc(from.ToDateTime(TimeOnly.MinValue));
        var endUtc = RomeCalendar.StartOfDayUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue));

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
                Completed = g.Count(r =>
                    (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato)
                    && r.CompletedAt >= startUtc && r.CompletedAt < endUtc),
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
        var monthEnd = new DateOnly(from.Year, from.Month, 1).AddMonths(1).AddDays(-1);
        var startUtc = RomeCalendar.StartOfDayUtc(from);
        var endUtc = RomeCalendar.StartOfDayUtc(monthEnd.AddDays(1));

        var month = await CompletedInOf(db, supplierOrgId, startUtc, endUtc).SingleOrDefaultAsync(cancellationToken);
        var toCollect = await ToCollectOf(db, supplierOrgId).SingleOrDefaultAsync(cancellationToken);

        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-SupplierEarningsSummary.ResponseWindowDays);
        var answers = await AnswersOf(db, supplierOrgId, since).ToListAsync(cancellationToken);
        var averageMinutes = SupplierResponseTimes.AverageMinutes(answers.Select(answer => answer.Minutes).ToList());

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

    public async Task<ShowcaseRequestStats> GetShowcaseRequestStatsAsync(
        Guid supplierOrgId,
        int days,
        CancellationToken cancellationToken = default)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);

        var counts = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && r.RentalContext == ServiceRequestRentalContext.Showcase
                        && r.CreatedAt >= since)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Requests = g.Count(),
                Pending = g.Count(r => r.Status == ServiceRequestStatus.Richiesto),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return new ShowcaseRequestStats(days, counts?.Requests ?? 0, counts?.Pending ?? 0);
    }

    public Task<int> CountJobsDoneAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId)
            .CountAsync(
                r => r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato,
                cancellationToken);

    public async Task<int?> GetMedianResponseMinutesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-SupplierEarningsSummary.ResponseWindowDays);
        var answers = await RecentAnswersOf(db, supplierOrgId, since, PublicShowcaseLimits.ResponseTimeMaxSamples)
            .ToListAsync(cancellationToken);
        return SupplierResponseTimes.PublicMedianMinutes(answers.Select(answer => answer.Minutes).ToList());
    }

    public async Task<IReadOnlyDictionary<Guid, int?>> GetMedianResponseMinutesBatchAsync(
        IReadOnlyCollection<Guid> supplierOrgIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplierOrgIds);

        var medians = supplierOrgIds.Distinct().ToDictionary(id => id, _ => (int?)null);
        if (medians.Count == 0)
            return medians;

        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-SupplierEarningsSummary.ResponseWindowDays);
        var answers = await AnswersOfMany(db, medians.Keys.ToList(), since).ToListAsync(cancellationToken);
        foreach (var bySupplier in answers.GroupBy(answer => answer.SupplierOrgId))
        {
            var latest = bySupplier
                .OrderByDescending(answer => answer.TakenAt)
                .Take(PublicShowcaseLimits.ResponseTimeMaxSamples)
                .Select(answer => (answer.TakenAt - answer.CreatedAt).TotalMinutes)
                .ToList();
            medians[bySupplier.Key] = SupplierResponseTimes.PublicMedianMinutes(latest);
        }

        return medians;
    }

    internal static IQueryable<AmountGroup> CompletedInOf(AppDbContext db, Guid supplierOrgId, DateTime startUtc, DateTime endUtc) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId
                        && (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato)
                        && r.CompletedAt >= startUtc
                        && r.CompletedAt < endUtc)
            .GroupBy(_ => 1)
            .Select(g => new AmountGroup(g.Count(), g.Sum(r => (long?)r.FinalAmountCents)));

    internal static IQueryable<AmountGroup> ToCollectOf(AppDbContext db, Guid supplierOrgId) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && r.Status == ServiceRequestStatus.Completato)
            .GroupBy(_ => 1)
            .Select(g => new AmountGroup(g.Count(), g.Sum(r => (long?)r.FinalAmountCents)));

    /// <summary>
    /// When each request taken since <paramref name="sinceUtc"/> was received and taken, for the average response time. A request
    /// accepted automatically (SP-04d) is born taken, so its wait would be zero and would falsify the average: it is left out.
    /// </summary>
    internal static IQueryable<AnswerPair> AnswersOf(AppDbContext db, Guid supplierOrgId, DateTime sinceUtc) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && !r.AutoAccepted && r.TakenAt != null && r.TakenAt >= sinceUtc)
            .Select(r => new AnswerPair(r.CreatedAt, r.TakenAt!.Value));

    /// <summary>
    /// The latest <paramref name="limit"/> requests of the supplier taken since <paramref name="sinceUtc"/>, when each was
    /// received and taken: the sample of the public response time (a median needs the pairs, not a sum). The requests accepted
    /// automatically (SP-04d) are left out, as in <see cref="AnswersOf"/>.
    /// </summary>
    internal static IQueryable<AnswerPair> RecentAnswersOf(AppDbContext db, Guid supplierOrgId, DateTime sinceUtc, int limit) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && !r.AutoAccepted && r.TakenAt != null && r.TakenAt >= sinceUtc)
            .OrderByDescending(r => r.TakenAt)
            .Take(limit)
            .Select(r => new AnswerPair(r.CreatedAt, r.TakenAt!.Value));

    /// <summary>
    /// When each request taken since <paramref name="sinceUtc"/> was received and taken, for every supplier in
    /// <paramref name="supplierOrgIds"/> at once: the sample of the response time of the supplier picker (SR-09a). The requests
    /// accepted automatically (SP-04d) are left out, as in <see cref="RecentAnswersOf"/>: nobody answered them.
    /// </summary>
    internal static IQueryable<SupplierAnswerPair> AnswersOfMany(AppDbContext db, IReadOnlyCollection<Guid> supplierOrgIds, DateTime sinceUtc) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => supplierOrgIds.Contains(r.SupplierOrgId) && !r.AutoAccepted && r.TakenAt != null && r.TakenAt >= sinceUtc)
            .Select(r => new SupplierAnswerPair(r.SupplierOrgId, r.CreatedAt, r.TakenAt!.Value));

    internal sealed record AmountGroup(int Jobs, long? Amount);

    internal sealed record SupplierAnswerPair(Guid SupplierOrgId, DateTime CreatedAt, DateTime TakenAt);

    internal sealed record AnswerPair(DateTime CreatedAt, DateTime TakenAt)
    {
        public double Minutes => (TakenAt - CreatedAt).TotalMinutes;
    }
}
