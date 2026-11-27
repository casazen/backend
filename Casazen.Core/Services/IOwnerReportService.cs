using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The performance of the properties an agency manages, cut by property owner (NF-96, <c>GET /api/reports/by-owner</c>). The figures are
/// the ones of <see cref="IHostDashboardService.GetPerformanceAsync"/> (read once for the whole scope, never a second rule) weighted by
/// what each owner holds of each property, so a co-owned property counts once in the grand total and the owners together with the
/// properties nobody owns add up to the Home's total for the same days.
/// </summary>
public interface IOwnerReportService
{
    /// <summary>
    /// The report by owner for the days of <paramref name="query"/> (<see cref="HostPerformanceQuery.From"/> to
    /// <see cref="HostPerformanceQuery.To"/>, checked by <see cref="HostReportRules.CheckPeriod"/> before this is called), of the
    /// properties <paramref name="scope"/> reaches. <see cref="HostPerformanceQuery.PropertyId"/> and
    /// <see cref="HostPerformanceQuery.PropertyIds"/> narrow it as they do the performance report; the cut and the comparison with the
    /// year before are not asked of this report (it is always cut by owner and gives the period only).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">As <see cref="IHostDashboardService.GetPerformanceAsync"/>.</exception>
    Task<OwnerPerformanceReport> GetByOwnerAsync(HostScope scope, HostPerformanceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The figures of a performance report weighted by an ownership share (NF-96): the sums of what the owner holds of each property, so the
/// nights and the stays can be fractions. ADR, RevPAR and the occupancy are worked out from the weighted values, never by averaging the
/// ratios of the properties. The values are not rounded: the web layer rounds what it shows.
/// </summary>
/// <param name="Revenue">Euros.</param>
/// <param name="OccupiedNights">Property-nights taken, as the Home counts them.</param>
/// <param name="AvailableNights">Property-nights available.</param>
/// <param name="ClosedNights">Property-nights closed only by the host's manual blocks.</param>
/// <param name="StayCount">Confirmed stays with a night in the period.</param>
/// <param name="RevenueNights">The nights of those stays that fall in the period: what the revenue is pro rata of.</param>
public sealed record OwnerPerformanceFigures(
    decimal Revenue,
    decimal OccupiedNights,
    decimal AvailableNights,
    decimal ClosedNights,
    decimal StayCount,
    decimal RevenueNights)
{
    /// <summary>No property, no night, no money.</summary>
    public static OwnerPerformanceFigures Zero { get; } = new(0m, 0m, 0m, 0m, 0m, 0m);

    /// <summary>Occupied / available (0..1); null when nothing is available.</summary>
    public decimal? OccupancyRate => AvailableNights > 0m ? OccupiedNights / AvailableNights : null;

    /// <summary>Revenue over <see cref="RevenueNights"/>, to the cent; null without a night (<see cref="HostPerformanceRules.Adr"/>).</summary>
    public decimal? Adr => HostPerformanceRules.Adr(Revenue, RevenueNights);

    /// <summary>Revenue over <see cref="AvailableNights"/>, to the cent; null when nothing is available (<see cref="HostPerformanceRules.RevPar"/>).</summary>
    public decimal? RevPar => HostPerformanceRules.RevPar(Revenue, AvailableNights);

    /// <summary>The figures of a property, counted by <paramref name="weight"/> (0..1): 1 for all of them, 0.5 for a half.</summary>
    public static OwnerPerformanceFigures Of(HostPerformanceFigures figures, decimal weight = 1m)
    {
        ArgumentNullException.ThrowIfNull(figures);

        return new OwnerPerformanceFigures(
            figures.Revenue * weight,
            figures.OccupiedNights * weight,
            figures.AvailableNights * weight,
            figures.ClosedNights * weight,
            figures.StayCount * weight,
            figures.RevenueNights * weight);
    }

    /// <summary>The sum of two sets of figures.</summary>
    public OwnerPerformanceFigures Plus(OwnerPerformanceFigures other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new OwnerPerformanceFigures(
            Revenue + other.Revenue,
            OccupiedNights + other.OccupiedNights,
            AvailableNights + other.AvailableNights,
            ClosedNights + other.ClosedNights,
            StayCount + other.StayCount,
            RevenueNights + other.RevenueNights);
    }
}

/// <summary>One owner of the report, or the properties nobody owns in the period.</summary>
/// <param name="OwnerId">The owner of the registry (<c>PropertyOwner</c>); null for the properties nobody owns.</param>
/// <param name="DisplayName">First and last name; null for an owner whose data was anonymized (<paramref name="IsAnonymized"/>) and for the row of the properties nobody owns.</param>
/// <param name="IsAnonymized">The owner's personal data was anonymized (GDPR): the web layer shows the anonymized label, never the name.</param>
/// <param name="PropertyCount">The properties of the report the owner holds a part of in the period.</param>
/// <param name="Figures">The figures of those properties, weighted by what the owner holds of each.</param>
public sealed record OwnerPerformanceRow(
    Guid? OwnerId,
    string? DisplayName,
    bool IsAnonymized,
    int PropertyCount,
    OwnerPerformanceFigures Figures);

/// <summary>The performance of the Home cut by owner.</summary>
/// <param name="Period">The days of the report.</param>
/// <param name="Rows">The owners that hold a part of a property of the report in the period, by name.</param>
/// <param name="Unassigned">
/// The properties nobody owns in the period, and the part of a property no owner holds: so the owners and this row add up to the Home's
/// total for the same days.
/// </param>
/// <param name="Total">
/// The whole scope, as the Home gives it for the same days: worked out over all the stays at once, so it can differ by a cent from the
/// sum of the rows.
/// </param>
public sealed record OwnerPerformanceReport(
    HostDashboardPeriod Period,
    IReadOnlyList<OwnerPerformanceRow> Rows,
    OwnerPerformanceRow Unassigned,
    OwnerPerformanceFigures Total);
