using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Supplier;

// ─── Requests ────────────────────────────────────────────────────────────────

public class CheckInRequest
{
    [Required]
    public string Token { get; set; } = string.Empty;

    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
}

public class CreateSupplierJobRequest
{
    [Required, MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? PropertyAddress { get; set; }

    [Required]
    public DateTime ScheduledStartUtc { get; set; }

    [Required]
    public DateTime ScheduledEndUtc { get; set; }

    [Range(0.01, 100000)]
    public decimal Price { get; set; }
}

// ─── Responses ───────────────────────────────────────────────────────────────

public class SupplierJobDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PropertyAddress { get; set; }
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime ScheduledEndUtc { get; set; }
    public decimal Price { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public string? CheckInUrl { get; set; }
}

public class CheckInStatusDto
{
    public Guid JobId { get; set; }
    public string PropertyAddress { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime ScheduledEndUtc { get; set; }
    public bool CanCheckIn { get; set; }
    public bool CanCheckOut { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
}
