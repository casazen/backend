using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("WireBatchStatements")]
public class WireBatchStatement : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid WireBatchId { get; set; }
    public virtual WireBatch WireBatch { get; set; } = null!;

    public Guid OwnerStatementId { get; set; }
    public virtual OwnerStatement OwnerStatement { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
