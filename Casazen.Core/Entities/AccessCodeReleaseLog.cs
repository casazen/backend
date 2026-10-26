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

    /// <summary>Tenant key (ITenantOwned). Inherited from the booking's property.</summary>
    public Guid OrgId { get; set; }

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>
    /// How the code was released: "screen" (guest session), "email" (automated send).
    /// NEVER includes the code itself.
    /// </summary>
    [Required, MaxLength(50)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>When the code was released to the guest (UTC).</summary>
    public DateTime ReleasedAt { get; set; } = DateTime.UtcNow;
}
