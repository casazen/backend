using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Execution log for automation routines (NF-26, ADR-022, AC-NF26-8).
/// Tracks each routine execution for a given event, ensuring idempotence.
/// Unique constraint on (RoutineId, EventId) ensures no duplicate execution.
/// </summary>
[Table("RoutineExecutions")]
public class RoutineExecution : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey("Routine")]
    public Guid RoutineId { get; set; }
    public virtual Routine? Routine { get; set; }

    /// <summary>
    /// Event identifier that triggered this execution.
    /// For domain events: BookingId, PaymentId, etc.
    /// For time-based events: composite key including date/time.
    /// Unique per (RoutineId, EventId) ensures idempotence (AC-NF26-9, ADR-022-R7).
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string EventId { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    [Required]
    [MaxLength(50)]
    public string Outcome { get; set; } = "Pending";

    public DateTime? ResumeAt { get; set; }

    public string ResultJson { get; set; } = "{}";
}
