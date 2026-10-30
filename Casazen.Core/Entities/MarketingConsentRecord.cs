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

    /// <summary>Tenant of the row: the org of the booking (TN-2).</summary>
    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    /// <summary>
    /// Marketing email address (may differ from booking contact email).
    /// Used for marketing campaigns when EmailConfirmed is true.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string MarketingEmail { get; set; } = string.Empty;

    /// <summary>Consent to receive marketing emails (art. 6.1.a GDPR).</summary>
    public bool ConsentEmail { get; set; }

    /// <summary>Consent to receive marketing WhatsApp messages (art. 6.1.a GDPR).</summary>
    public bool ConsentWhatsApp { get; set; }

    /// <summary>
    /// Double opt-in confirmation status: true when the guest clicked the confirmation link in the
    /// verification email (ADR-007-R17, R21). Marketing sends are blocked until this is true.
    /// </summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>When the guest submitted the consent on the booking checkout.</summary>
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
