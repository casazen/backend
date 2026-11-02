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
}

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
