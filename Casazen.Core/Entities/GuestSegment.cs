using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Marketing segment for guest targeting (NF-24, ADR-014).
/// Stores condition builder definition in JSON for server-side evaluation.
/// </summary>
[Table("GuestSegments")]
public class GuestSegment : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org that owns this segment (TN-1).</summary>
    public Guid OrgId { get; set; }

    /// <summary>User-friendly segment name (e.g., "Repeat guests").</summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Condition definition JSON (ADR-014-R1): stored as jsonb for server-side evaluation.
    /// Schema: {version: "1.0", groups: [{operator: "AND"|"OR", rules: [{fieldId, operator, value}]}]}
    /// </summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public string ConditionJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
