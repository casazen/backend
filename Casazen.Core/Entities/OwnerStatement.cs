using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("OwnerStatements")]
public class OwnerStatement : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid PropertyOwnerId { get; set; }
    public virtual PropertyOwner PropertyOwner { get; set; } = null!;

    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public int Month { get; set; }

    public int Year { get; set; }

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    public StatementStatus Status { get; set; } = StatementStatus.Draft;

    [Precision(18, 2)]
    public decimal TotalRevenue { get; set; }

    [Precision(18, 2)]
    public decimal Commission { get; set; }

    [Precision(18, 2)]
    public decimal Expenses { get; set; }

    [Precision(18, 2)]
    public decimal WithholdingTax { get; set; }

    [Precision(18, 2)]
    public decimal NetPayable { get; set; }

    [Precision(5, 2)]
    public decimal OwnershipPercent { get; set; } = 100.00m;

    public DateTime? PublishedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<StatementLine> Lines { get; set; } = new List<StatementLine>();
}
