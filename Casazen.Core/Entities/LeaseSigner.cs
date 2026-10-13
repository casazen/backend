using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A party as signer of the lease contract (LT-02, A7-16): one row per party once a signature path has started, so the
/// provider signing links survive a page refresh and the signature of each party is recorded. The offline path writes
/// the rows as signed when the landlord uploads the signed contract; the provider path writes them with the personal
/// link and its expiry, and the provider webhook marks them signed. No personal data: the party is referenced by id.
/// </summary>
[Table("LeaseSigners")]
public class LeaseSigner : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key, the lease's org.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required]
    public Guid LeaseContractId { get; set; }

    [ForeignKey(nameof(LeaseContractId))]
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    [Required]
    public Guid PartyId { get; set; }

    [ForeignKey(nameof(PartyId))]
    public virtual Party Party { get; set; } = null!;

    [Required]
    public LeaseSignatureMethod Method { get; set; }

    [Required]
    public LeaseSignerStatus Status { get; set; } = LeaseSignerStatus.Pending;

    /// <summary>Provider id of the signer (matches the webhook events); null offline.</summary>
    [MaxLength(200)]
    public string? ExternalSignerId { get; set; }

    /// <summary>Personal signing link of the provider, shown to the landlord only while pending; null offline.</summary>
    [MaxLength(2000)]
    public string? SigningUrl { get; set; }

    /// <summary>Expiry of <see cref="SigningUrl"/> (UTC instant).</summary>
    public DateTime? SigningUrlExpiresAt { get; set; }

    /// <summary>
    /// When the party signed: the provider event instant, or the declared stipula date (midnight UTC of the Rome date)
    /// for an offline signature.
    /// </summary>
    public DateTime? SignedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
