using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

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

    /// <summary>
    /// Manual (the supplier) or iCal feed (the sync), SU-15. The sync frees only its own days; rows written before SU-15
    /// are <see cref="SupplierAvailabilitySource.Manual"/> (their origin is unknown, see <c>docs/runbooks/ical.md</c>).
    /// </summary>
    public SupplierAvailabilitySource Source { get; set; } = SupplierAvailabilitySource.Manual;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
