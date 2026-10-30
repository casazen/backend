using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Suppression list entry for marketing campaigns (NF-05): stores SHA-256 blind index of email addresses
/// that opted out via one-click unsubscribe or GDPR request. The blind index allows checking if an email
/// is suppressed without storing the actual email address (privacy by design).
/// </summary>
/// <remarks>
/// ADR-007-R18, R22: One-click unsubscribe via HMAC-signed link adds entries here.
/// The EmailHash is a SHA-256 (not HMAC) hash of the normalized email address.
/// On GDPR deletion, the guest's personal data is removed but the hash remains to prevent re-subscription.
/// </remarks>
[Table("SuppressionListEntries")]
public class SuppressionListEntry : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the guest (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// SHA-256 blind index of the normalized email address (lowercase, trimmed).
    /// Allows suppression checking without storing the plaintext email.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string EmailHash { get; set; } = string.Empty;

    /// <summary>When this email was added to the suppression list.</summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Source of the suppression: "unsubscribe" (one-click link), "manual" (host action), "gdpr" (deletion request).
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Source { get; set; } = string.Empty;
}
