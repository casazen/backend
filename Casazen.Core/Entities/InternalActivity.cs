using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Internal activity of an org, of two kinds that share the table <c>InternalActivities</c>:
/// <list type="bullet">
/// <item>the <b>staff work</b> (NF-22): cleaning, maintenance or check-in assistance performed by a staff user
/// (collaborator with "Only some" perimeter) on a property, optionally recharged to the owner. It has no
/// <see cref="Action"/>;</item>
/// <item>an item of the <b>"To do" list</b> (ADR-026, NF-07): something the host has to do, with the
/// <see cref="Action"/> its "Do now" button leads to, the entity it is about and its <see cref="Urgency"/>. It always has an
/// <see cref="Action"/>. It is closed (by the system when its condition is met, or by a user) with <see cref="Status"/>
/// "Completed", <see cref="CompletedAt"/> and <see cref="ClosedBy"/>, and the closed row is kept for the audit.</item>
/// </list>
/// </summary>
[Table("InternalActivities")]
public class InternalActivity : ITenantOwned
{
    public const string StatusPending = "Pending";

    public const string StatusInProgress = "InProgress";

    public const string StatusCompleted = "Completed";

    public const string StatusCancelled = "Cancelled";

    public const string CleaningActivityType = "Cleaning";

    public const string ClosedBySystem = "system";

    public const string BookingEntityType = "Booking";

    public const string OpenTodoIndexName = "UIX_InternalActivities_OpenTodo";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Property")]
    public Guid? PropertyId { get; set; }
    public virtual Property? Property { get; set; }

    [ForeignKey("Booking")]
    public Guid? BookingId { get; set; }
    public virtual Booking? Booking { get; set; }

    [MaxLength(255)]
    public string? StaffUserId { get; set; }

    [Required, MaxLength(50)]
    public string ActivityType { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal? InternalRate { get; set; }

    public bool RechargeToOwner { get; set; }

    [Required, MaxLength(20)]
    public string Status { get; set; } = StatusPending;

    [MaxLength(2000)]
    public string? Notes { get; set; }

    [Required]
    public DateTime ScheduledAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string? CreatedByUserId { get; set; }

    public ActivityAction? Action { get; set; }

    [MaxLength(50)]
    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public ActivityUrgency? Urgency { get; set; }

    public DateTime? Deadline { get; set; }

    /// <summary>Why a "To do" was raised, in one line (an error code, a short text): never personal data.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    [MaxLength(255)]
    public string? ClosedBy { get; set; }
}
