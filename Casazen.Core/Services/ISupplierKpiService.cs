using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// Work KPIs of a supplier org, computed from its service requests (SU-11, A4-15): the only supplier work model since
/// the supplier jobs were removed (decision D12). Since SP-04 also the earnings of the month, what is left to collect, the
/// average time to answer and whether the supplier ever answered a request (the numbers of "Oggi" and its checklist).
/// </summary>
public interface ISupplierKpiService
{
    /// <summary>
    /// KPIs of the service requests assigned to <paramref name="supplierOrgId"/> (and to no other org). "Today" and the
    /// period are Europe/Rome calendar dates.
    /// </summary>
    Task<SupplierServiceRequestKpis> GetKpisAsync(
        Guid supplierOrgId,
        SupplierKpiPeriod period,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The numbers of the console home for the supplier (SP-04): the earnings of the current Europe/Rome month and the amount
    /// still to collect, from the <b>final amounts</b> the supplier declared when it completed its jobs, and the average time
    /// it takes to answer. Read-only.
    /// </summary>
    Task<SupplierEarningsSummary> GetEarningsSummaryAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the supplier ever answered a request: it took one (it has a <c>TakenAt</c>) or rejected one. The step "answer the
    /// first request" of the console checklist (SP-04).
    /// </summary>
    Task<bool> HasAnsweredARequestAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The typical time the supplier takes to answer, for the public showcase (SP-09): the <b>median</b> of
    /// <c>TakenAt − CreatedAt</c> over the requests taken in the last <see cref="SupplierEarningsSummary.ResponseWindowDays"/>
    /// days (the same pairs the average of <see cref="GetEarningsSummaryAsync"/> is made of), in whole minutes. <c>null</c> until
    /// there are <see cref="PublicShowcaseLimits.ResponseTimeMinSamples"/> of them: a response time is shown only when it is
    /// measured, never written by hand. The requests accepted automatically (SP-04d) are not among them: nobody answered them.
    /// Read-only.
    /// </summary>
    Task<int?> GetMedianResponseMinutesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="GetMedianResponseMinutesAsync"/> for several suppliers in one read (the supplier picker of the marketplace, SR-09a):
    /// the same window of 90 days, the same sample (the latest <see cref="PublicShowcaseLimits.ResponseTimeMaxSamples"/> requests
    /// taken by each supplier) and the same minimum of <see cref="PublicShowcaseLimits.ResponseTimeMinSamples"/> requests. Every id
    /// asked about is in the result; a supplier whose response time is not measured has <c>null</c>. Read-only; independent of the
    /// <c>SupplierShowcaseBooking</c> flag.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int?>> GetMedianResponseMinutesBatchAsync(
        IReadOnlyCollection<Guid> supplierOrgIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The three numbers of the console home with the period before them (SP-04e, <c>GET api/supplier/dashboard/overview</c>):
    /// what the supplier received, accepted and refused, how often it said yes, how long it took to answer, the jobs it completed
    /// and the money that came in, for <paramref name="period"/> and for the same number of Europe/Rome days just before it.
    /// <paramref name="period"/> is one of <see cref="SupplierKpiPeriods.OverviewPeriods"/>. Read-only; scoped by the supplier org.
    /// </summary>
    Task<SupplierOverview> GetOverviewAsync(
        Guid supplierOrgId,
        SupplierKpiPeriod period,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The overview of the console home for a period and the one before it (SP-04e). The definitions of each figure are in
/// <see cref="SupplierOverviewPeriod"/> and in <c>docs/runbooks/suppliers.md</c> § 27.
/// </summary>
/// <param name="Period">The period asked for.</param>
/// <param name="OnlinePayments">
/// The supplier has money that goes through CasaZen to show: the flag <c>SupplierOnlinePayments</c> is on and the supplier has at least
/// one payment. When false the console talks about the jobs completed, not about "incassi", and the money figures are 0.
/// </param>
/// <param name="Current">The period asked for.</param>
/// <param name="Previous">The same number of days just before it.</param>
public sealed record SupplierOverview(
    SupplierKpiPeriod Period,
    bool OnlinePayments,
    SupplierOverviewPeriod Current,
    SupplierOverviewPeriod Previous);

/// <summary>
/// The figures of one period of the overview, every day an Europe/Rome calendar day (both ends included). A figure that does not
/// exist (a rate with nobody answered, an average with no answer) is <c>null</c>, never 0.
/// </summary>
/// <param name="From">First day.</param>
/// <param name="To">Last day.</param>
/// <param name="Received">Requests that arrived (by <c>CreatedAt</c>), whatever became of them.</param>
/// <param name="Accepted">Requests the supplier took (by <c>TakenAt</c>); a request cancelled after the take stays accepted.</param>
/// <param name="Rejected">Requests the supplier refused (by the date of the refusal).</param>
/// <param name="AcceptanceRate">Accepted over accepted plus rejected, from 0 to 1 with four decimals; null when the supplier answered none.</param>
/// <param name="AverageResponseMinutes">
/// Average of <c>TakenAt − CreatedAt</c> of the requests taken in the period, in whole minutes (the same pairs as
/// <c>GET api/supplier/today</c>); null when it took none. A request accepted automatically (SP-04d) does not count: it is born taken,
/// so its wait would be zero (it counts as accepted in <paramref name="Accepted"/>).
/// </param>
/// <param name="CompletedJobs">Jobs completed (<c>Completato</c>, or <c>Pagato</c> afterwards), by completion date.</param>
/// <param name="PaidNetCents">The net of the payments paid in the period (by <c>PaidAt</c>); 0 unless <see cref="SupplierOverview.OnlinePayments"/>.</param>
/// <param name="PaidGrossCents">The gross of the payments paid in the period; 0 unless <see cref="SupplierOverview.OnlinePayments"/>.</param>
/// <param name="CommissionCents">The commission CasaZen kept on them; 0 unless <see cref="SupplierOverview.OnlinePayments"/>.</param>
/// <param name="ToCollectCents">
/// What is still to be collected <b>now</b> from the jobs completed and not paid: the declared total of a job paid outside CasaZen, the
/// net of the payment of one paid inside it. Only in the current period; null in the previous one.
/// </param>
/// <param name="ToCollectJobs">How many jobs are still to be collected now. Only in the current period; null in the previous one.</param>
public sealed record SupplierOverviewPeriod(
    DateOnly From,
    DateOnly To,
    int Received,
    int Accepted,
    int Rejected,
    double? AcceptanceRate,
    int? AverageResponseMinutes,
    int CompletedJobs,
    long PaidNetCents,
    long PaidGrossCents,
    long CommissionCents,
    long? ToCollectCents,
    int? ToCollectJobs);

/// <summary>Service-request KPIs of a supplier org.</summary>
/// <param name="Period">The requested period.</param>
/// <param name="From">First Europe/Rome calendar date of the period (included).</param>
/// <param name="To">Last Europe/Rome calendar date of the period (included).</param>
/// <param name="Completed">Requests completed in the period (<c>Completato</c>, or <c>Pagato</c> afterwards), by completion date.</param>
/// <param name="Rejected">Requests the supplier rejected in the period, by rejection date.</param>
/// <param name="AwaitingAcceptance">Requests waiting for the supplier to take them (<c>Richiesto</c>) now, whatever the period.</param>
/// <param name="Upcoming">Requests taken and not completed yet (<c>PresoInCarico</c>, <c>InCorso</c>) now, whatever the period.</param>
/// <param name="TotalRequests">Every request ever assigned to the supplier org, in any state (cancelled ones included).</param>
public record SupplierServiceRequestKpis(
    SupplierKpiPeriod Period,
    DateOnly From,
    DateOnly To,
    int Completed,
    int Rejected,
    int AwaitingAcceptance,
    int Upcoming,
    int TotalRequests);

/// <summary>
/// The money of the supplier's home (SP-04). Until the payments of SP-15 exist these are the supplier's own declarations (the
/// final amounts of the jobs it completed), not money received: <see cref="IsEstimate"/> says so and the console labels the
/// earnings "stimato".
/// </summary>
/// <param name="MonthFrom">First day of the current Europe/Rome month.</param>
/// <param name="MonthTo">Last day of the current Europe/Rome month.</param>
/// <param name="MonthAmountCents">Sum of the final amounts of the jobs completed this month (<c>Completato</c> or <c>Pagato</c>, by completion date), in cents.</param>
/// <param name="MonthJobs">Jobs completed this month, with or without an amount.</param>
/// <param name="ToCollectAmountCents">Sum of the final amounts of the completed jobs not marked as paid yet (<c>Completato</c>), whatever the month.</param>
/// <param name="ToCollectJobs">Completed jobs not marked as paid yet.</param>
/// <param name="AverageResponseMinutes">
/// Average time from the arrival of a request to its take (<c>TakenAt − CreatedAt</c>) over the requests taken in the last
/// <see cref="ResponseWindowDays"/> days, in whole minutes; <c>null</c> when there are none. The requests accepted automatically (SP-04d)
/// are left out: nobody answered them.
/// </param>
/// <param name="IsEstimate">True while the amounts are declarations and not payments (always, until SP-15).</param>
public sealed record SupplierEarningsSummary(
    DateOnly MonthFrom,
    DateOnly MonthTo,
    long MonthAmountCents,
    int MonthJobs,
    long ToCollectAmountCents,
    int ToCollectJobs,
    int? AverageResponseMinutes,
    bool IsEstimate)
{
    /// <summary>The window of the average response time: the requests taken in the last 90 days.</summary>
    public const int ResponseWindowDays = 90;
}
