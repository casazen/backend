using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Per-date availability flag for a supplier org (US-022 / #292, AC8).
/// </summary>
[Table("SupplierAvailability")]
public class SupplierAvailability
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid OrgId { get; set; }

    [Required]
    public DateOnly Date { get; set; }

    public bool Available { get; set; } = true;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
