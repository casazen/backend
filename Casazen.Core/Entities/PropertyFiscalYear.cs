using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("PropertyFiscalYears")]
public class PropertyFiscalYear
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public int TaxYear { get; set; }

    public StrFiscalRegime Regime { get; set; }

    public bool IsPrimaryForCedolare { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
