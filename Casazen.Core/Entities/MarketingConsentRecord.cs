using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Marketing consent record per booking guest (NF-05): stores per-channel consent (email, WhatsApp),
/// double opt-in confirmation status, and the GDPR-required metadata (timestamp, IP address, consent text version).
/// The MarketingEmail field is separate from the booking contact email to allow guests to specify a different
/// email for marketing purposes.
/// </summary>
[Table("MarketingConsentRecords")]
public class MarketingConsentRecord : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    [Required]
    [MaxLength(255)]
    public string MarketingEmail { get; set; } = string.Empty;

    public bool ConsentEmail { get; set; }

    public bool ConsentWhatsApp { get; set; }

    public bool EmailConfirmed { get; set; }

    public DateTime ConsentTimestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Version identifier of the consent text presented to the guest (e.g., "v1.0", "2026-10-25").
    /// Supports GDPR accountability (art. 7.1) by proving which terms the guest agreed to.
    /// </summary>
    [MaxLength(100)]
    public string ConsentTextVersion { get; set; } = string.Empty;

    /// <summary>
    /// Client IP address when the consent was given (GDPR proof of consent).
    /// Cleared on anonymization after retention period.
    /// </summary>
    [MaxLength(50)]
    public string IpAddress { get; set; } = string.Empty;
}
