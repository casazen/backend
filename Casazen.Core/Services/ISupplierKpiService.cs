using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// Work KPIs of a supplier org, computed from its service requests (SU-11, A4-15): the only supplier work model since
/// the supplier jobs were removed (decision D12).
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
}

/// <summary>Service-request KPIs of a supplier org.</summary>
/// <param name="Period">The requested period.</param>
/// <param name="From">First Europe/Rome calendar date of the period (included).</param>
/// <param name="To">Last Europe/Rome calendar date of the period (included).</param>
/// <param name="Completed">Requests completed in the period (<c>Completato</c>, or <c>Pagato</c> afterwards), by completion date.</param>
/// <param name="Rejected">Requests the supplier rejected in the period, by rejection date.</param>
/// <param name="AwaitingAcceptance">Requests waiting for the supplier to take them (<c>Richiesto</c>) now, whatever the period.</param>
/// <param name="Upcoming">Requests taken and not completed yet (<c>PresoInCarico</c>, <c>InCorso</c>) now, whatever the period.</param>
/// <param name="TotalRequests">Every request ever assigned to the supplier org, in any state.</param>
public record SupplierServiceRequestKpis(
    SupplierKpiPeriod Period,
    DateOnly From,
    DateOnly To,
    int Completed,
    int Rejected,
    int AwaitingAcceptance,
    int Upcoming,
    int TotalRequests);
