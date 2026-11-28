using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("Routines")]
public class Routine : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? AgentObjective { get; set; }

    public RoutineEventType EventType { get; set; }

    public string EventConfig { get; set; } = "{}";

    public string ConditionJson { get; set; } = "{}";

    [Required]
    public string ActionsJson { get; set; } = "[]";

    public bool TestMode { get; set; } = false;

    public bool Active { get; set; } = true;
}
