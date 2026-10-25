using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.OwnerPortal;

/// <summary>
/// Property summary for owner portal (ADR-011-R8): basic property info with occupancy and revenue.
/// Guest data is masked per ADR-011-R3.
/// </summary>
public class OwnerPortalPropertyDto
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int TotalBookings { get; set; }
    public decimal TotalRevenue { get; set; }
    public double OccupancyRate { get; set; }
    public IReadOnlyList<OwnerPortalBookingSummaryDto> RecentBookings { get; set; } = [];
}

/// <summary>
/// Booking summary with guest data masked (ADR-011-R3): names NULL, emails NULL, phones NULL.
/// Only dates, channel, and amounts visible.
/// </summary>
public class OwnerPortalBookingSummaryDto
{
    public Guid BookingId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string Channel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; }
}

/// <summary>
/// Monthly statement summary for owner portal (ADR-011-R2).
/// Only published statements are visible.
/// </summary>
public class OwnerPortalStatementDto
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
/// Invoice summary for owner portal (ADR-011-R2).
/// Invoices received by the owner from the agency.
/// </summary>
public class OwnerPortalInvoiceDto
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsPaid { get; set; }
}

/// <summary>
/// Withholding tax summary for owner portal (ADR-011-R2).
/// Aggregated withholding information for tax purposes.
/// </summary>
public class OwnerPortalWithholdingDto
{
    public int Year { get; set; }
    public decimal TotalWithheld { get; set; }
    public IReadOnlyList<OwnerPortalWithholdingDetailDto> Details { get; set; } = [];
}

/// <summary>
/// Detailed withholding entry.
/// </summary>
public class OwnerPortalWithholdingDetailDto
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public decimal AmountWithheld { get; set; }
}
