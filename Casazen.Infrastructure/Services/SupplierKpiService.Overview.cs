using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The overview of the console home (SP-04e): the numbers of a period next to those of the period before it. The definitions are in
/// <see cref="SupplierOverviewPeriod"/> and in <c>docs/runbooks/suppliers.md</c> § 27; the money comes from the payment records
/// (<see cref="ISupplierPaymentReadModels"/>), the rest from the requests, always with the supplier org as an explicit predicate.
/// </summary>
public partial class SupplierKpiService
{
    public async Task<SupplierOverview> GetOverviewAsync(
        Guid supplierOrgId,
        SupplierKpiPeriod period,
        CancellationToken cancellationToken = default)
    {
        if (!SupplierKpiPeriods.OverviewPeriods.Contains(period))
            throw new ArgumentOutOfRangeException(nameof(period), period, "Not a period of the overview.");

        var (from, to) = SupplierKpiPeriods.Resolve(period, timeProvider.TodayInRomeAsDateOnly());
        var (previousFrom, previousTo) = SupplierKpiPeriods.Previous(from, to);

        // Each period is made of Europe/Rome calendar days: [00:00 Rome of the first, 00:00 Rome of the day after the last). The two
        // are next to each other, so the end of the previous one is the start of the current one.
        var previousStart = RomeCalendar.StartOfDayUtc(previousFrom);
        var currentStart = RomeCalendar.StartOfDayUtc(from);
        var currentEnd = RomeCalendar.StartOfDayUtc(to.AddDays(1));

        var counts = await OverviewCountsOf(db, supplierOrgId, previousStart, currentStart, currentEnd)
            .SingleOrDefaultAsync(cancellationToken);

        // Read as pairs and averaged here, like the home does, so the arithmetic on instants does not depend on the provider.
        var answers = await AnswersBetween(db, supplierOrgId, previousStart, currentEnd).ToListAsync(cancellationToken);
        var currentWaits = answers.Where(a => a.TakenAt >= currentStart).Select(Minutes).ToList();
        var previousWaits = answers.Where(a => a.TakenAt < currentStart).Select(Minutes).ToList();

        // Still to collect, now: the declared total of a job paid outside CasaZen, the net of the payment of one paid inside it.
        var open = await OpenJobsOf(db, supplierOrgId).ToListAsync(cancellationToken);
        var nets = payments is null
            ? new Dictionary<Guid, int>()
            : (await payments.NetToCollectAsync(supplierOrgId, open.Select(job => job.Id).ToList(), cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        var toCollectCents = open.Sum(job => (long)(nets.TryGetValue(job.Id, out var net) ? net : job.FinalAmountCents ?? 0));

        // The money of the home: only when the supplier has payments through CasaZen to show (the flag is on and there is one).
        var onlinePayments = false;
        var currentPaid = SupplierPaymentTotals.None;
        var previousPaid = SupplierPaymentTotals.None;
        if (features is not null
            && payments is not null
            && features.IsEnabled(FeatureFlags.SupplierOnlinePayments)
            && await payments.HasPaymentsAsync(supplierOrgId, cancellationToken))
        {
            onlinePayments = true;
            currentPaid = await payments.PaidBetweenAsync(supplierOrgId, currentStart, currentEnd, cancellationToken);
            previousPaid = await payments.PaidBetweenAsync(supplierOrgId, previousStart, currentStart, cancellationToken);
        }

        counts ??= OverviewCounts.Empty;
        return new SupplierOverview(
            period,
            onlinePayments,
            Build(from, to, counts.ReceivedCurrent, counts.AcceptedCurrent, counts.RejectedCurrent, counts.CompletedCurrent, currentWaits, currentPaid, toCollectCents, open.Count),
            Build(previousFrom, previousTo, counts.ReceivedPrevious, counts.AcceptedPrevious, counts.RejectedPrevious, counts.CompletedPrevious, previousWaits, previousPaid, null, null));
    }

    private static SupplierOverviewPeriod Build(
        DateOnly from,
        DateOnly to,
        int received,
        int accepted,
        int rejected,
        int completed,
        IReadOnlyCollection<double> waits,
        SupplierPaymentTotals paid,
        long? toCollectCents,
        int? toCollectJobs) =>
        new(
            from,
            to,
            received,
            accepted,
            rejected,
            SupplierAcceptance.Rate(accepted, rejected),
            SupplierResponseTimes.AverageMinutes(waits),
            completed,
            paid.NetCents,
            paid.GrossCents,
            paid.CommissionCents,
            toCollectCents,
            toCollectJobs);

    private static double Minutes(AnswerPair answer) => (answer.TakenAt - answer.CreatedAt).TotalMinutes;

    /// <summary>
    /// What came in, was answered and was completed in each of the two periods, counted by the database in one pass over the requests of
    /// the supplier: arrived (by <c>CreatedAt</c>), taken (by <c>TakenAt</c>, whatever became of it after), refused (a final status: its
    /// last update is the refusal, like the KPIs of SU-11) and completed (<c>Completato</c> or <c>Pagato</c>, by <c>CompletedAt</c>).
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<OverviewCounts> OverviewCountsOf(
        AppDbContext db,
        Guid supplierOrgId,
        DateTime previousStart,
        DateTime currentStart,
        DateTime currentEnd) =>
        // ServiceRequest has no tenant filter (two parties, TN-2 allow-list): scoped by the supplier org explicitly.
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId)
            .GroupBy(_ => 1)
            .Select(g => new OverviewCounts(
                g.Count(r => r.CreatedAt >= currentStart && r.CreatedAt < currentEnd),
                g.Count(r => r.CreatedAt >= previousStart && r.CreatedAt < currentStart),
                g.Count(r => r.TakenAt >= currentStart && r.TakenAt < currentEnd),
                g.Count(r => r.TakenAt >= previousStart && r.TakenAt < currentStart),
                g.Count(r => r.Status == ServiceRequestStatus.Rifiutato && r.UpdatedAt >= currentStart && r.UpdatedAt < currentEnd),
                g.Count(r => r.Status == ServiceRequestStatus.Rifiutato && r.UpdatedAt >= previousStart && r.UpdatedAt < currentStart),
                g.Count(r => (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato)
                             && r.CompletedAt >= currentStart && r.CompletedAt < currentEnd),
                g.Count(r => (r.Status == ServiceRequestStatus.Completato || r.Status == ServiceRequestStatus.Pagato)
                             && r.CompletedAt >= previousStart && r.CompletedAt < currentStart)));

    /// <summary>When each request taken inside <c>[fromUtc, toUtc)</c> was received and taken, for the average response time.</summary>
    internal static IQueryable<AnswerPair> AnswersBetween(AppDbContext db, Guid supplierOrgId, DateTime fromUtc, DateTime toUtc) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && r.TakenAt != null && r.TakenAt >= fromUtc && r.TakenAt < toUtc)
            .Select(r => new AnswerPair(r.CreatedAt, r.TakenAt!.Value));

    /// <summary>The jobs of the supplier completed and not paid yet, with the total each declared.</summary>
    internal static IQueryable<OpenJob> OpenJobsOf(AppDbContext db, Guid supplierOrgId) =>
        db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.SupplierOrgId == supplierOrgId && r.Status == ServiceRequestStatus.Completato)
            .Select(r => new OpenJob(r.Id, r.FinalAmountCents));

    /// <summary>The counts of the two periods of the overview.</summary>
    internal sealed record OverviewCounts(
        int ReceivedCurrent,
        int ReceivedPrevious,
        int AcceptedCurrent,
        int AcceptedPrevious,
        int RejectedCurrent,
        int RejectedPrevious,
        int CompletedCurrent,
        int CompletedPrevious)
    {
        /// <summary>A supplier without a single request.</summary>
        public static OverviewCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>A completed job not paid yet and the total its supplier declared (<c>null</c> when it declared none).</summary>
    internal sealed record OpenJob(Guid Id, int? FinalAmountCents);
}
