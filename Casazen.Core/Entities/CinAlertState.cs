using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// How far the CIN alert went for one property (CO-20, A5-31): the daily <c>cin-deadline-alert</c> job alerts the host
/// only when it moves <see cref="Stage"/> forward, with an atomic compare-and-set on this row, so a stage is sent once
/// whatever the number of runs, retries or concurrent runs. One row per property.
/// </summary>
[Table("CinAlertStates")]
[Index(nameof(PropertyId), IsUnique = true)]
public class CinAlertState : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>
    /// Deadline (<c>Cin:ExposureDeadline</c>) the stage refers to; null = no deadline configured (reminder of the
    /// obligation). When the configured deadline changes, the sequence starts again for the new one.
    /// </summary>
    public DateOnly? Deadline { get; set; }

    public int? Stage { get; set; }

    public int AlertCount { get; set; }

    public DateTime? LastAlertAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
