using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// What is wrong with the supplier's jobs to come for a range of days (SP-03b, <c>GET api/supplier/calendar/conflicts</c>).
/// </summary>
/// <param name="From">First Europe/Rome day looked at.</param>
/// <param name="To">Last Europe/Rome day looked at (included).</param>
/// <param name="Items">The jobs that have a conflict, by start (a job with no conflict is not listed).</param>
/// <param name="Truncated">
/// True when the range held more than <see cref="SupplierAgendaLimits.MaxConflictRequests"/> jobs to check: only the nearest were looked at.
/// </param>
public sealed record SupplierConflictReport(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SupplierRequestConflicts> Items,
    bool Truncated);

/// <summary>
/// The conflicts of the supplier's agenda (SP-03b): which of its jobs to come are in trouble, why, and when it could be done
/// instead. A <b>read</b> that decides nothing and writes nothing: the supplier decides (decision SP-D28: a time outside the
/// published hours is not refused, the console says why it is outside). The rules are the planner's own, asked through
/// <see cref="SupplierConflictDetector"/>, so the planner stays the only source of truth.
/// </summary>
/// <remarks>
/// <b>Tenancy.</b> The supplier org id comes from the caller's own supplier link, never from the request; every read goes through
/// <see cref="ISupplierAgendaService"/>, <see cref="ISupplierServiceRequestReader"/> and <see cref="ISupplierServiceCatalogService"/>,
/// whose statements carry the explicit supplier predicate.
/// </remarks>
public interface ISupplierConflictService
{
    /// <summary>
    /// The conflicts of the requests with hours that fall from <paramref name="from"/> to <paramref name="to"/> (Europe/Rome days,
    /// both included, at most <see cref="SupplierAgendaLimits.MaxCalendarDays"/>). Read-only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is reversed or longer than the limit (the controller answers 400 before).</exception>
    Task<SupplierConflictReport> GetConflictsAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}
