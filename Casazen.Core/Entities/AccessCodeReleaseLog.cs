using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Audit log of access code releases to guests (NF-13, E06/F03/US01/T01).
/// Records when and how a code was released, NEVER the code itself (AC-NF13-9).
/// Only created after biometric/operator verification passes.
/// </summary>
[Table("AccessCodeReleaseLogs")]
public class AccessCodeReleaseLog : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    [Required, MaxLength(50)]
    public string Channel { get; set; } = string.Empty;

    public DateTime ReleasedAt { get; set; } = DateTime.UtcNow;
}
