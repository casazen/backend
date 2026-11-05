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
    /// <summary>The <see cref="Status"/> of an open activity.</summary>
    public const string StatusPending = "Pending";

    /// <summary>The <see cref="Status"/> of a done activity (a closed "To do").</summary>
    public const string StatusCompleted = "Completed";

    /// <summary>The <see cref="ClosedBy"/> of a "To do" closed by the system (its condition was met).</summary>
    public const string ClosedBySystem = "system";

    /// <summary>The <see cref="EntityType"/> of a "To do" about a booking (<see cref="EntityId"/> = the booking id).</summary>
    public const string BookingEntityType = "Booking";

    /// <summary>The unique index that keeps one open "To do" per type and entity in an org.</summary>
    public const string OpenTodoIndexName = "UIX_InternalActivities_OpenTodo";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant key: inherited from the property, or, for an organization-level "To do" (no property), the org it was
    /// raised for.
    /// </summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>
    /// The property of the activity. Always set for the staff work; <c>null</c> for an organization-level "To do" (a lost
    /// channel connection), which only an org-wide scope sees.
    /// </summary>
    [ForeignKey("Property")]
    public Guid? PropertyId { get; set; }
    public virtual Property? Property { get; set; }

    /// <summary>Optional booking this activity is related to.</summary>
    [ForeignKey("Booking")]
    public Guid? BookingId { get; set; }
    public virtual Booking? Booking { get; set; }

    /// <summary>
    /// The staff user (collaborator with "Only some" perimeter) assigned to the staff work; for a "To do", the person it is
    /// for, when it is for one. Never in the answer of the "To do" list.
    /// </summary>
    [MaxLength(255)]
    public string? StaffUserId { get; set; }

    /// <summary>
    /// Type of activity: Cleaning, Maintenance, CheckIn for the staff work; the name of an
    /// <see cref="Enums.ActivityType"/> member for a "To do".
    /// </summary>
    [Required, MaxLength(50)]
    public string ActivityType { get; set; } = string.Empty;

    /// <summary>Internal rate per activity or per person (optional).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? InternalRate { get; set; }

    /// <summary>Whether the cost should be recharged to the property owner.</summary>
    public bool RechargeToOwner { get; set; }

    /// <summary>Status: Pending, InProgress, Completed.</summary>
    [Required, MaxLength(20)]
    public string Status { get; set; } = StatusPending;

    /// <summary>Notes about the activity.</summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// When the activity is scheduled (UTC). For a "To do", its <see cref="Deadline"/> or, without one, the time it was
    /// created.
    /// </summary>
    [Required]
    public DateTime ScheduledAt { get; set; }

    /// <summary>When the activity was completed (UTC); for a "To do", when it was closed (the ClosedAt of ADR-026).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When the activity was created (UTC).</summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who created the activity (User.Id).</summary>
    [MaxLength(255)]
    public string? CreatedByUserId { get; set; }

    /// <summary>Where the "Do now" button of a "To do" leads; <c>null</c> on the staff work (ADR-026-R6).</summary>
    public ActivityAction? Action { get; set; }

    /// <summary>The kind of entity a "To do" is about (<see cref="BookingEntityType"/>, ...); the frontend reads its detail.</summary>
    [MaxLength(50)]
    public string? EntityType { get; set; }

    /// <summary>The id of the entity a "To do" is about.</summary>
    public Guid? EntityId { get; set; }

    /// <summary>How urgent a "To do" is.</summary>
    public ActivityUrgency? Urgency { get; set; }

    /// <summary>When a "To do" is due (UTC), when it has a deadline (the DueAt of ADR-026).</summary>
    public DateTime? Deadline { get; set; }

    /// <summary>Why a "To do" was raised, in one line (an error code, a short text): never personal data.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    /// <summary>Who closed a "To do": <see cref="ClosedBySystem"/> or the id of the user.</summary>
    [MaxLength(255)]
    public string? ClosedBy { get; set; }
}
