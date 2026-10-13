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
    /// measured, never written by hand. Read-only.
    /// </summary>
    Task<int?> GetMedianResponseMinutesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);
}

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
/// <see cref="ResponseWindowDays"/> days, in whole minutes; <c>null</c> when there are none.
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
