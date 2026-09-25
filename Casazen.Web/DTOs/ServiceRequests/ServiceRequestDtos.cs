using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs.ServiceRequests;

/// <summary>
/// Body of <c>POST api/service-requests</c> (short-rent: <see cref="BookingId"/> required, a stay of the property) and
/// <c>POST api/long-rent/service-requests</c> (long-rent: no <see cref="BookingId"/>), decision D2 (SU-07).
/// </summary>
/// <remarks>
/// Format and length (A4-18, SU-10): 400 <c>validation_error</c> with localized field errors. The category must be one
/// of the codes of <c>GET /api/service-categories</c>: a value that is not a code is refused by the service with 422
/// <c>invalid_service_category</c> (SU-03).
/// </remarks>
public class CreateServiceRequestRequest
{
    [NotEmptyGuid(ErrorMessage = "ServiceRequestPropertyRequired")]
    public Guid PropertyId { get; set; }

    [NotEmptyGuid(ErrorMessage = "ServiceRequestBookingInvalid")]
    public Guid? BookingId { get; set; }

    [NotEmptyGuid(ErrorMessage = "ServiceRequestSupplierRequired")]
    public Guid SupplierOrgId { get; set; }

    [Required(ErrorMessage = "ServiceCategoryRequired")]
    public string Category { get; set; } = string.Empty;

    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;

    /// <summary>At most 1000 characters (the column of <c>ServiceRequest.Notes</c>).</summary>
    [MaxLength(1000, ErrorMessage = "ServiceRequestNotesTooLong")]
    public string? Notes { get; set; }

    public bool ChargeToGuest { get; set; }
}

/// <summary>Body of <c>POST api/service-requests/{id}/reject</c>: the reason is required (A4-18).</summary>
public class RejectServiceRequestRequest
{
    /// <summary>Why the supplier refuses the request, shown to the host: required, at most 500 characters.</summary>
    [Required(ErrorMessage = "ServiceRequestRejectReasonRequired")]
    [MaxLength(500, ErrorMessage = "ServiceRequestRejectReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Optional body of <c>POST api/service-requests/{id}/complete</c>.</summary>
public class CompleteServiceRequestRequest
{
    /// <summary>Replaces the request's notes when sent: at most 1000 characters.</summary>
    [MaxLength(1000, ErrorMessage = "ServiceRequestNotesTooLong")]
    public string? Notes { get; set; }
}

/// <summary>
/// Body of <c>POST api/service-requests/match-supplier</c>. No free text: the host's notes are not accepted, so they
/// can never reach an AI prompt (A8-15); the category is one of the known codes (A8-01).
/// </summary>
public class MatchSupplierRequest
{
    [NotEmptyGuid(ErrorMessage = "ServiceRequestPropertyRequired")]
    public Guid PropertyId { get; set; }

    [Required(ErrorMessage = "ServiceCategoryRequired")]
    [ServiceCategoryCode(ErrorMessage = "ServiceCategoryNotSupported")]
    public string Category { get; set; } = string.Empty;

    [EnumDataType(typeof(ServiceRequestUrgency))]
    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;
}

public class SupplierMatchCandidateDto
{
    public Guid OrgId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Bio { get; set; }
    public int MatchScore { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public string Source { get; set; } = "platform";
}

public class ExternalSupplierSuggestionDto
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public double? Rating { get; set; }
    public int? ReviewCount { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string Source { get; set; } = "ai_web_search";
}

public class SupplierMatchResponse
{
    public SupplierMatchCandidateDto? Recommended { get; set; }
    public IEnumerable<SupplierMatchCandidateDto> Alternatives { get; set; } = [];
    public IEnumerable<ExternalSupplierSuggestionDto> ExternalSuggestions { get; set; } = [];
    public bool UsedExternalFallback { get; set; }
}

public class ServiceRequestDto
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? BookingId { get; set; }

    /// <summary><c>ShortRent</c> (per stay) or <c>LongRent</c> (per property), decision D2.</summary>
    public string RentalContext { get; set; } = string.Empty;
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

public class ServiceRequestListResponse
{
    public IEnumerable<ServiceRequestDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
