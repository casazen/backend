using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// A stretch of hours that changes the supplier's agenda (SP-03): hours the supplier <b>blocks</b> by hand, an <b>extra
/// opening</b> on top of the weekly hours, or an <b>external</b> engagement of the supplier's own calendar (the iCal feed
/// writes those from SP-05). Instants in UTC; the table is read as a list of intervals by the planner.
/// </summary>
/// <remarks>
/// <para><b>Who writes what.</b> The console API (<c>api/supplier/availability/blocks</c>) creates and deletes only
/// <see cref="SupplierBusyWindowSource.Manual"/> windows of kind <see cref="SupplierBusyWindowKind.Block"/> or
/// <see cref="SupplierBusyWindowKind.ExtraOpening"/>; <see cref="SupplierBusyWindowKind.External"/> windows come from the
/// sync of the iCal feed (SP-05), under the same lock, and the supplier cannot delete them.</para>
/// <para>Keyed by the supplier org and not tenant-filtered, like <see cref="SupplierWorkingHours"/> (same reasons, same
/// guard). The <see cref="Label"/> is shown only in the supplier's console, <b>never public</b>: a public read (SP-09)
/// must not expose it (nor the kind of a window: a customer only sees that the hours are not free).</para>
/// </remarks>
[Table("SupplierBusyWindows")]
public class SupplierBusyWindow
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The supplier org (<see cref="SupplierProfile.OrgId"/>).</summary>
    [Required]
    public Guid OrgId { get; set; }

    /// <summary>First instant of the window (UTC).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Instant the window ends (UTC), after <see cref="StartUtc"/>.</summary>
    public DateTime EndUtc { get; set; }

    public SupplierBusyWindowKind Kind { get; set; } = SupplierBusyWindowKind.Block;

    public SupplierBusyWindowSource Source { get; set; } = SupplierBusyWindowSource.Manual;

    /// <summary>What the supplier calls it ("Dentista"); optional, at most 80 characters, shown only in the supplier's console.</summary>
    [MaxLength(SupplierAgendaLimits.LabelMaxLength)]
    public string? Label { get; set; }

    /// <summary>The UID of the iCal event an <see cref="SupplierBusyWindowKind.External"/> window comes from (SP-05); null for the others.</summary>
    [MaxLength(SupplierAgendaLimits.ExternalUidMaxLength)]
    public string? ExternalUid { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
