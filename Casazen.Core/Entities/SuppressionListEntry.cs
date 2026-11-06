using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Suppression list entry for marketing campaigns (NF-05): stores a blind index of email addresses
/// that opted out via one-click unsubscribe, GDPR request or the host recording the guest's withdrawal. The blind index allows checking if
/// an email is suppressed without storing the actual email address (privacy by design).
/// </summary>
/// <remarks>
/// ADR-007-R18, R22: One-click unsubscribe via HMAC-signed link adds entries here.
/// <para><b>The EmailHash is a keyed index for the new rows (E07/F06/US01/T02, NF-95):</b> the HMAC-SHA256 of the normalized email address
/// (lowercase hex, 64 characters) keyed by <c>Crm:ContactIndexKey</c>, computed by <c>ICrmContactIndex</c>, like
/// <c>ServiceCustomers.EmailHash</c>. The rows made before it hold the plain SHA-256 of the same normalized form: they are <b>legacy rows,
/// never deleted and never rewritten</b> (the address is not there to compute the keyed index from), and the suppression check reads both
/// forms so nobody who opted out is written to again. Same column, no schema change.</para>
/// <para>A row is <b>sticky</b>: nothing clears it, and no later consent does (re-subscription is not specified).
/// On GDPR deletion, the guest's personal data is removed but the hash remains to prevent re-subscription.</para>
/// </remarks>
[Table("SuppressionListEntries")]
public class SuppressionListEntry : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the guest (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// Blind index of the normalized email address (lowercase, trimmed): the keyed HMAC-SHA256 for a new row, the plain SHA-256 for a legacy
    /// row (see the remarks of the class). Allows suppression checking without storing the plaintext email.
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
