using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

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

    public SupplierAvailabilitySource Source { get; set; } = SupplierAvailabilitySource.Manual;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
