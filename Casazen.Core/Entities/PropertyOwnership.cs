using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Links a property to one of its owners with their share percentage (ADR-012-R4).
/// Multiple PropertyOwnership rows for the same PropertyId define co-ownership: the sum
/// of SharePercent must equal 100.00 (validated by PropertyOwnershipService).
/// OwnershipStart and OwnershipEnd preserve the history per NF-03 requirement.
/// </summary>
[Table("PropertyOwnerships")]
public class PropertyOwnership : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (ADR-012-R4). Inherited from the property; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The property being owned.</summary>
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>The owner of the property.</summary>
    public Guid PropertyOwnerId { get; set; }
    public virtual PropertyOwner PropertyOwner { get; set; } = null!;

    /// <summary>
    /// Links to OwnerUser for owner portal access (ADR-011-R1). Nullable because not all
    /// PropertyOwner entries have been invited to the portal yet.
    /// </summary>
    public Guid? OwnerUserId { get; set; }
    public virtual OwnerUser? OwnerUser { get; set; }

    /// <summary>
    /// Ownership share as a percentage (0.00 to 100.00). Stored as decimal(5,2) in PostgreSQL.
    /// The sum of shares for a PropertyId MUST equal 100.00 (ADR-012-R4).
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal SharePercent { get; set; }

    /// <summary>When this ownership started (UTC). Preserves history per NF-03.</summary>
    public DateTime OwnershipStart { get; set; }

    /// <summary>
    /// When this ownership ended (UTC). Null = active ownership.
    /// Preserves history per NF-03.
    /// </summary>
    public DateTime? OwnershipEnd { get; set; }
}
