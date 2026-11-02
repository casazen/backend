using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Join table tracking which owner statements are included in which wire transfer batches.
/// Ensures each statement appears in exactly one batch (ADR-017 AC-NF19-8).
/// </summary>
[Table("WireBatchStatements")]
public class WireBatchStatement : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the batch; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The wire batch this statement is part of.</summary>
    public Guid WireBatchId { get; set; }
    public virtual WireBatch WireBatch { get; set; } = null!;

    /// <summary>The owner statement included in this batch.</summary>
    public Guid OwnerStatementId { get; set; }
    public virtual OwnerStatement OwnerStatement { get; set; } = null!;

    /// <summary>When this statement was added to the batch (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
