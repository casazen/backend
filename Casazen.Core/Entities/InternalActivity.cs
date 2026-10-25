using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Internal activity performed by staff (collaborator with "Only some" perimeter) on properties
/// (NF-22). Activities like cleaning, maintenance, or check-in assistance can be assigned to
/// staff users and optionally recharged to the owner.
/// </summary>
[Table("InternalActivities")]
public class InternalActivity : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key, inherited from the property.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Optional booking this activity is related to.</summary>
    [ForeignKey("Booking")]
    public Guid? BookingId { get; set; }
    public virtual Booking? Booking { get; set; }

    /// <summary>The staff user (collaborator with "Only some" perimeter) assigned to this activity.</summary>
    [Required, MaxLength(255)]
    public string StaffUserId { get; set; } = string.Empty;

    /// <summary>Type of activity: Cleaning, Maintenance, CheckIn.</summary>
    [Required, MaxLength(50)]
    public string ActivityType { get; set; } = string.Empty;

    /// <summary>Internal rate per activity or per person (optional).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? InternalRate { get; set; }

    /// <summary>Whether the cost should be recharged to the property owner.</summary>
    public bool RechargeToOwner { get; set; }

    /// <summary>Status: Pending, InProgress, Completed.</summary>
    [Required, MaxLength(20)]
    public string Status { get; set; } = "Pending";

    /// <summary>Notes about the activity.</summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>When the activity is scheduled (UTC).</summary>
    [Required]
    public DateTime ScheduledAt { get; set; }

    /// <summary>When the activity was completed (UTC).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When the activity was created (UTC).</summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who created the activity (User.Id).</summary>
    [MaxLength(255)]
    public string? CreatedByUserId { get; set; }
}
