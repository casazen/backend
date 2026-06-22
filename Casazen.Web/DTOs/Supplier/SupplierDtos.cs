using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Supplier;

// ─── Requests ────────────────────────────────────────────────────────────────

public class SupplierRegisterRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string LegalName { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string ComuneCode { get; set; } = string.Empty;

    public string? InviteToken { get; set; }
}

public class UpdateSupplierProfileRequest
{
    [MaxLength(300)]
    public string? LegalName { get; set; }

    [MaxLength(20)]
    public string? VatNumber { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    public IEnumerable<string>? Categories { get; set; }

    public IEnumerable<string>? Comuni { get; set; }

    [MaxLength(2000)]
    public string? Bio { get; set; }

    public IEnumerable<string>? PhotoUrls { get; set; }
}

public class CompleteActivationRequest
{
    [Required]
    public bool TosAccepted { get; set; }
}

public class UpdateAvailabilityRequest
{
    [Required]
    public IEnumerable<AvailabilityEntryDto> Dates { get; set; } = [];
}

public class AvailabilityEntryDto
{
    [Required]
    public DateOnly Date { get; set; }

    public bool Available { get; set; } = true;
}

public class AdminInviteSupplierRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string ComuneCode { get; set; } = string.Empty;

    public IEnumerable<string>? Categories { get; set; }

    [MaxLength(1000)]
    public string? Message { get; set; }
}

// ─── Responses ───────────────────────────────────────────────────────────────

public class SupplierRegisterResponse
{
    public Guid OrgId { get; set; }
    public string AuthRedirectUrl { get; set; } = string.Empty;
}

public class SupplierProfileDto
{
    public Guid OrgId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string? VatNumber { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public IEnumerable<string> Categories { get; set; } = [];
    public IEnumerable<string> Comuni { get; set; } = [];
    public string? Bio { get; set; }
    public IEnumerable<string> PhotoUrls { get; set; } = [];
    public DateTime? TosAcceptedAt { get; set; }
}

public class ActivationStatusDto
{
    public string Status { get; set; } = string.Empty;
    public IEnumerable<ActivationStepDto> Steps { get; set; } = [];
}

public class ActivationStepDto
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Blocker { get; set; }
}

public class CompleteActivationResponse
{
    public string Status { get; set; } = string.Empty;
}

public class SupplierInboxResponse
{
    public IEnumerable<object> Items { get; set; } = [];
    public int Total { get; set; }
}

public class UpdateAvailabilityResponse
{
    public int Updated { get; set; }
}

public class AdminInviteResponse
{
    public Guid InviteId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class SupplierPickerDto
{
    public Guid OrgId { get; set; }
    public string LegalName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public IEnumerable<string> Categories { get; set; } = [];
    public IEnumerable<string> Comuni { get; set; } = [];
    public string? Bio { get; set; }
    public IEnumerable<string> PhotoUrls { get; set; } = [];
}
