using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// Days the supplier does not work (SP-03, <c>api/supplier/availability/time-off</c>): holidays, a public holiday, an
/// illness. Every day from <see cref="FromDate"/> to <see cref="ToDate"/> (both included, Europe/Rome calendar days) is
/// closed to new bookings; the jobs already accepted stay.
/// </summary>
/// <remarks>
/// Keyed by the supplier org and not tenant-filtered, like <see cref="SupplierWorkingHours"/> (same reasons, same guard).
/// The <see cref="Label"/> is shown only to the supplier: it is never public.
/// </remarks>
[Table("SupplierTimeOff")]
public class SupplierTimeOff
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid OrgId { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public SupplierTimeOffReason Reason { get; set; } = SupplierTimeOffReason.Vacation;

    [MaxLength(SupplierAgendaLimits.LabelMaxLength)]
    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
