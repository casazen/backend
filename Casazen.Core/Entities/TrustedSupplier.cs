using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("TrustedSuppliers")]
public class TrustedSupplier : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("SupplierOrg")]
    public Guid SupplierOrgId { get; set; }
    public virtual Org SupplierOrg { get; set; } = null!;

    public List<Guid> AssignedPropertyIds { get; set; } = new();

    public int Priority { get; set; }

    public bool VisibleInMarketplace { get; set; }

    public bool? HostOrigin { get; set; }

    public DateTime? RemovedAt { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; }
}
