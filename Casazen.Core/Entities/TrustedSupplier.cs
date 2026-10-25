using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Trusted supplier relationship for agency tier (ADR-020): an agency's preferred supplier
/// for service work. Trusted suppliers work with PaymentMode.Manual and 0% commission,
/// and the approved amount flows to the owner statement (NF-17).
/// </summary>
[Table("TrustedSuppliers")]
public class TrustedSupplier : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The agency org that trusts this supplier.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The supplier's org.</summary>
    [ForeignKey("SupplierOrg")]
    public Guid SupplierOrgId { get; set; }
    public virtual Org SupplierOrg { get; set; } = null!;

    /// <summary>Properties this supplier may work on (empty = all properties of the agency).</summary>
    public List<Guid> AssignedPropertyIds { get; set; } = new();

    /// <summary>Sort order for supplier list (lower = higher priority).</summary>
    public int Priority { get; set; }

    /// <summary>Whether this supplier appears in the agency's marketplace.</summary>
    public bool VisibleInMarketplace { get; set; }

    /// <summary>When the trust relationship was created (UTC).</summary>
    [Required]
    public DateTime CreatedAt { get; set; }
}
