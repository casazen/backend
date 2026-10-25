using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// CRM profile for guest matching and marketing (NF-23, ADR-021): aggregates a guest's stays, spend,
/// marketing consent and tags across bookings. Matched by SHA-256 composite key of normalized guest data.
/// Excludes Alloggiati submission data per ADR-021-R5.
/// </summary>
[Table("GuestCrmProfiles")]
public class GuestCrmProfile : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the bookings (TN-2).</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>
    /// SHA-256 hash of normalized composite key: email|name|surname|dob|fiscalCode (ADR-021-R2).
    /// Used for guest matching across bookings.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string CompositeKeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Confirmed marketing email address (double opt-in verified).
    /// Populated from MarketingConsentRecord when EmailConfirmed is true.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string ConfirmedMarketingEmail { get; set; } = string.Empty;

    /// <summary>Consent to receive marketing emails (aggregated from latest consent record).</summary>
    public bool MarketingConsentEmail { get; set; }

    /// <summary>Consent to receive marketing WhatsApp messages (aggregated from latest consent record).</summary>
    public bool MarketingConsentWhatsApp { get; set; }

    /// <summary>Guest's preferred language for marketing communications.</summary>
    [MaxLength(10)]
    public string Language { get; set; } = string.Empty;

    /// <summary>Guest's country (ISO 3166-1 alpha-2 or full name).</summary>
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;

    /// <summary>Total number of stays across all bookings.</summary>
    public int TotalStays { get; set; }

    /// <summary>Total amount spent across all bookings.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSpend { get; set; }

    /// <summary>JSON array of property IDs where the guest has stayed (for HostScope filtering).</summary>
    [MaxLength(2000)]
    public string PropertyIdsJson { get; set; } = string.Empty;

    /// <summary>JSON array of tags assigned to the guest (max 50 per ADR-021-R9).</summary>
    [MaxLength(2000)]
    public string TagsJson { get; set; } = string.Empty;

    /// <summary>Date of the guest's first stay.</summary>
    public DateTime FirstStayAt { get; set; }

    /// <summary>Date of the guest's most recent stay.</summary>
    public DateTime LastStayAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Guest records linked to this CRM profile.</summary>
    public virtual ICollection<Guest> Guests { get; set; } = new List<Guest>();
}
