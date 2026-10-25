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

    /// <summary>Tenant: the org that owns this execution (TN-1).</summary>
    public Guid OrgId { get; set; }

    /// <summary>Routine that was executed.</summary>
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

    /// <summary>When the execution started (UTC).</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the execution completed or failed (UTC).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Execution outcome (Success, Failed, Waiting).</summary>
    [Required]
    [MaxLength(50)]
    public string Outcome { get; set; } = "Pending";

    /// <summary>
    /// Resume time for Wait actions (AC-NF26-10, ADR-022-R10).
    /// When set, a Hangfire job will resume the routine at this time.
    /// </summary>
    public DateTime? ResumeAt { get; set; }

    /// <summary>
    /// Execution result as JSON.
    /// Logs actions taken, errors encountered, simulation results in test mode.
    /// </summary>
    public string ResultJson { get; set; } = "{}";
}
