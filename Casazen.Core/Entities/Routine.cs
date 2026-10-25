using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Automation routine (NF-26, ADR-022).
/// Defines a workflow triggered by an event, evaluated against conditions, and executing actions.
/// </summary>
[Table("Routines")]
public class Routine : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org that owns this routine (TN-1).</summary>
    public Guid OrgId { get; set; }

    /// <summary>Routine name (user-defined).</summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Event type that triggers this routine (AC-NF26-2).</summary>
    public RoutineEventType EventType { get; set; }

    /// <summary>
    /// Event configuration as JSON.
    /// For time-based events (BeforeCheckin, AfterCheckin, etc.): { "days": N, "hours": N }.
    /// For recurrence: { "dayOfWeek": N }.
    /// </summary>
    public string EventConfig { get; set; } = "{}";

    /// <summary>
    /// Condition definition JSON (ADR-014 format).
    /// Evaluated to determine if the routine should execute for a given event.
    /// </summary>
    public string ConditionJson { get; set; } = "{}";

    /// <summary>
    /// Actions definition as JSON array (AC-NF26-3).
    /// Each action: { "type": "RoutineActionType", ...config }.
    /// </summary>
    [Required]
    public string ActionsJson { get; set; } = "[]";

    /// <summary>
    /// Test mode flag (AC-NF26-7).
    /// When true, routine executes in simulation mode (logs actions without performing them).
    /// </summary>
    public bool TestMode { get; set; } = false;

    /// <summary>
    /// Active flag.
    /// Only active routines are triggered by events.
    /// </summary>
    public bool Active { get; set; } = true;
}
