using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.ServiceRequests;

public class CreateServiceRequestRequest
{
    public Guid PropertyId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid SupplierOrgId { get; set; }
    public string Category { get; set; } = string.Empty;
    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;
    public string? Notes { get; set; }
    public bool ChargeToGuest { get; set; }
}

public class RejectServiceRequestRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class CompleteServiceRequestRequest
{
    public string? Notes { get; set; }
}

public class ServiceRequestDto
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public Guid SupplierOrgId { get; set; }
    public string? SupplierName { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Urgency { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? TakenAt { get; set; }
    public string? TakenByUserId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool ChargeToGuest { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ServiceRequestSummaryDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Urgency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ServiceRequestListResponse
{
    public IEnumerable<ServiceRequestDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
