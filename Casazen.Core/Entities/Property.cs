using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Validation;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Properties")]
public class Property
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Tenant key (AC2). Server-set from the caller's org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>URL-friendly identifier unique within the org for direct booking links.</summary>
    [MaxLength(100)]
    public string? Slug { get; set; }

    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string City { get; set; } = string.Empty;

    [MaxLength(10)]
    public string PostalCode { get; set; } = string.Empty;

    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }

    [Range(1, 100, ErrorMessage = "Bedrooms must be between 1 and 100")]
    public int Bedrooms { get; set; }

    [Range(1, 50, ErrorMessage = "Bathrooms must be between 1 and 50")]
    public int Bathrooms { get; set; }

    [Range(1, 100, ErrorMessage = "Max guests must be between 1 and 100")]
    public int MaxGuests { get; set; }

    [Precision(18, 2)]
    [Range(0.01, 100000, ErrorMessage = "Nightly rate must be between €0.01 and €100,000")]
    public decimal NightlyRate { get; set; }

    [Precision(18, 2)]
    [Range(0, 10000, ErrorMessage = "Cleaning fee must be between €0 and €10,000")]
    public decimal CleaningFee { get; set; }

    [Precision(18, 2)]
    [Range(0, 50000, ErrorMessage = "Damage deposit must be between €0 and €50,000")]
    public decimal DamageDeposit { get; set; }

    public List<PropertyAmenity> Amenities { get; set; } = new();
    public List<string> PhotoUrls { get; set; } = new();

    [MaxLength(1000)]
    public string HouseRules { get; set; } = string.Empty;

    // Italian regulatory compliance - D.L. 145/2023
    [MaxLength(25)]
    [CinCode]
    public string? CinCode { get; set; } // Format: IT-XXXXX-XXXXXXXXXX

    // Timezone for booking date handling (IANA timezone ID)
    [MaxLength(50)]
    public string Timezone { get; set; } = "Europe/Rome"; // Default for Italian properties

    [ForeignKey("CancellationPolicy")]
    public Guid? CancellationPolicyId { get; set; }
    public virtual CancellationPolicy? CancellationPolicy { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Compliance activation gate for public listing (US-019 / #295).</summary>
    public PropertyComplianceStatus ComplianceStatus { get; set; } = PropertyComplianceStatus.Pending;

    public DateTime? ComplianceCompletedAt { get; set; }

    /// <summary>JSON safety checklist: smokeDetector, fireExtinguisher, gasCompliance, acknowledgedAt, acknowledgedBy.</summary>
    public string? SafetyChecklistJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public virtual ICollection<OtaIntegration> OtaIntegrations { get; set; } = new List<OtaIntegration>();
    public virtual ICollection<PropertyDocument> PropertyDocuments { get; set; } = new List<PropertyDocument>();
    public virtual PricingAdapterConfig? PricingAdapterConfig { get; set; }
}