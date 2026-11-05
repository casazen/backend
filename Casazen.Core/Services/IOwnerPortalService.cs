using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Owner portal data access service (ADR-011).
/// Filters by PropertyOwnership.OwnerUserId and active ownership period.
/// All responses mask guest data per ADR-011-R3.
/// </summary>
public interface IOwnerPortalService
{
    /// <summary>
    /// Gets properties owned by the owner with occupancy and revenue summaries (ADR-011-R2, ADR-011-R8).
    /// Filters by ownership period: OwnershipStart &lt;= now AND (OwnershipEnd == null OR OwnershipEnd &gt;= now).
    /// Guest data is masked in booking summaries.
    /// </summary>
    Task<IReadOnlyList<OwnerPortalPropertyInfo>> GetOwnerPropertiesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets published monthly statements for the owner's properties (ADR-011-R2).
    /// Returns empty list for now (statements feature not yet implemented).
    /// </summary>
    Task<IReadOnlyList<OwnerPortalStatementInfo>> GetOwnerStatementsAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets invoices received by the owner (ADR-011-R2).
    /// Returns empty list for now (invoices feature not yet implemented).
    /// </summary>
    Task<IReadOnlyList<OwnerPortalInvoiceInfo>> GetOwnerInvoicesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets withholding tax summary for the owner (ADR-011-R2).
    /// Returns empty summary for now (withholding feature not yet implemented).
    /// </summary>
    Task<OwnerPortalWithholdingInfo> GetOwnerWithholdingAsync(
        Guid ownerUserId,
        int? year = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that the owner has access to the specified property (ADR-011-R2).
    /// Returns true if PropertyOwnership exists with active period, false otherwise.
    /// </summary>
    Task<bool> OwnerHasAccessToPropertyAsync(
        Guid ownerUserId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The performance of the owner's properties on the Home's numbers (NF-96, <c>GET /api/owner-portal/overview</c>): occupancy,
    /// revenue, ADR and RevPAR, and next to them the same calendar days one year earlier when asked. The figures are those of
    /// <see cref="IHostDashboardService.GetPerformanceAsync"/> (the Home's stays, nights and rules, never a copy of them), read for
    /// each org the owner has properties in and <b>weighted by the share the owner holds</b> (<c>SharePercent</c>), so the owners of a
    /// co-owned property together are the Home's figure for it. Only the owner's own ownerships count: the ones in force today
    /// (<c>OwnershipStart</c> up to now, <c>OwnershipEnd</c> null or not before now) that started by the last day of the report and
    /// had not ended before its first. Guest data is never read.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <see cref="OwnerOverviewQuery.PropertyId"/> is not one of the owner's properties in those days (another owner's, an ended
    /// ownership, missing): the same 404 for all (ADR-011-R2).
    /// </exception>
    Task<OwnerPortalOverview> GetOwnerOverviewAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same figures week by week or month by month (NF-96, <c>GET /api/owner-portal/trend</c>), for all the owner's properties
    /// together or one line for each (at most <see cref="HostPerformanceRules.MaxTrendSeries"/>, the rest summed in <c>other</c>), each
    /// bucket next to its days one year earlier when asked. Same ownerships and same weights as <see cref="GetOwnerOverviewAsync"/>.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">As <see cref="GetOwnerOverviewAsync"/>.</exception>
    Task<OwnerPortalTrend> GetOwnerTrendAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the owner portal asks for its overview and its trend (NF-96): the days (both included, Europe/Rome dates, checked by
/// <see cref="HostReportRules.CheckPeriod"/> before the read), how the lines are cut (<see cref="HostPerformanceGroupBy.None"/> or
/// <see cref="HostPerformanceGroupBy.Property"/>: an owner has no channel cut) and whether the same days one year earlier come too.
/// </summary>
/// <param name="From">First day, included.</param>
/// <param name="To">Last day, included.</param>
/// <param name="GroupBy">The cut of the rows or lines.</param>
/// <param name="CompareYear">Also work out the same calendar days one year earlier.</param>
public sealed record OwnerOverviewQuery(
    DateOnly From,
    DateOnly To,
    HostPerformanceGroupBy GroupBy = HostPerformanceGroupBy.None,
    bool CompareYear = false)
{
    /// <summary>Only this property; null = every property of the owner. It must be one of the owner's.</summary>
    public Guid? PropertyId { get; init; }
}

/// <summary>One property of the owner's overview, or its total: the Home's figures weighted by the owner's share.</summary>
/// <param name="Key">A stable key: the property id, or <c>total</c>.</param>
/// <param name="PropertyId">The property of a property row; null for the total.</param>
/// <param name="Label">The name of the property; the key for the total.</param>
/// <param name="Figures">The figures of the days asked.</param>
/// <param name="PreviousYear">The same calendar days one year earlier; null when the overview did not compare and when there was nothing to compare with.</param>
public sealed record OwnerPortalRow(
    string Key,
    Guid? PropertyId,
    string Label,
    OwnerPerformanceFigures Figures,
    OwnerPerformanceFigures? PreviousYear);

/// <summary>The owner's properties on the Home's numbers (NF-96).</summary>
/// <param name="Period">The days of the overview.</param>
/// <param name="PreviousYearPeriod">The same calendar days one year earlier; null unless the overview compared.</param>
/// <param name="Rows">A row for each property of the owner that is active or had revenue, by name; none unless cut by property.</param>
/// <param name="Total">All the owner's properties together, weighted by the owner's shares.</param>
public sealed record OwnerPortalOverview(
    HostDashboardPeriod Period,
    HostDashboardPeriod? PreviousYearPeriod,
    IReadOnlyList<OwnerPortalRow> Rows,
    OwnerPortalRow Total);

/// <summary>One line of the owner's trend: all the properties together, or one of them (or <c>other</c>, the sum of the rest).</summary>
/// <param name="Key">A stable key: the property id, <c>total</c> or <c>other</c>.</param>
/// <param name="Label">The name of the property; the key for the others.</param>
/// <param name="PropertyId">The property of a property line; null for any other.</param>
/// <param name="Points">One entry for each bucket, in order, weighted by the owner's shares.</param>
/// <param name="PreviousYear">The same buckets one year earlier, null where there was nothing to compare with; empty unless the trend compared.</param>
public sealed record OwnerPortalSeries(
    string Key,
    string Label,
    Guid? PropertyId,
    IReadOnlyList<OwnerPerformanceFigures> Points,
    IReadOnlyList<OwnerPerformanceFigures?> PreviousYear);

/// <summary>The owner's trend (NF-96): the weeks or months of the period, the same buckets as the host's trend.</summary>
public sealed record OwnerPortalTrend(
    HostDashboardPeriod Period,
    HostDashboardPeriod? PreviousYearPeriod,
    HostOverviewSeriesUnit Unit,
    IReadOnlyList<HostTrendBucket> Buckets,
    IReadOnlyList<OwnerPortalSeries> Series);

/// <summary>
/// Property information for owner portal with masked guest data (ADR-011-R3).
/// </summary>
public class OwnerPortalPropertyInfo
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int TotalBookings { get; set; }
    public decimal TotalRevenue { get; set; }
    public double OccupancyRate { get; set; }
    public IReadOnlyList<OwnerPortalBookingInfo> RecentBookings { get; set; } = [];
}

/// <summary>
/// Booking information with guest data masked per ADR-011-R3.
/// Names NULL, emails NULL, phones NULL. Only dates, channel, amounts.
/// </summary>
public class OwnerPortalBookingInfo
{
    public Guid BookingId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string Channel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; }
}

/// <summary>
/// Monthly statement information (placeholder for future implementation).
/// </summary>
public class OwnerPortalStatementInfo
{
    public Guid StatementId { get; set; }
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public int Year { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal AgencyCommission { get; set; }
    public decimal NetAmount { get; set; }
    public DateTime PublishedAt { get; set; }
}

/// <summary>
/// Invoice information (placeholder for future implementation).
/// </summary>
public class OwnerPortalInvoiceInfo
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsPaid { get; set; }
}

/// <summary>
/// Withholding tax summary (placeholder for future implementation).
/// </summary>
public class OwnerPortalWithholdingInfo
{
    public int Year { get; set; }
    public decimal TotalWithheld { get; set; }
    public IReadOnlyList<OwnerPortalWithholdingDetailInfo> Details { get; set; } = [];
}

/// <summary>
/// Withholding detail (placeholder for future implementation).
/// </summary>
public class OwnerPortalWithholdingDetailInfo
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public decimal AmountWithheld { get; set; }
}
