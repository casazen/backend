using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Supplier invite (US-022 / #292, AC3): created by a platform admin, or by a host that invites one of its trusted suppliers
/// (<see cref="InvitedByOrgId"/>, bug #977). The signup link carries a random token
/// (<see cref="Casazen.Core.Suppliers.SupplierInviteTokens"/>) whose hash is <see cref="TokenHash"/>; the invite is
/// accepted once, before <see cref="ExpiresAt"/>, by a signed-in user with the invited <see cref="Email"/>, for
/// <see cref="ComuneCode"/> (SU-01).
/// </summary>
[Table("SupplierInviteRecords")]
public class SupplierInviteRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 (lowercase hex) of the link token; the token itself is never stored. Null for invites created before
    /// SU-01, whose link used the invite id as token: they can no longer be accepted.
    /// </summary>
    [MaxLength(64)]
    public string? TokenHash { get; set; }

    /// <summary>
    /// The comune the supplier registers for. Empty only for an invite of a host whose properties name no comune of the
    /// official list: the supplier then picks it while registering.
    /// </summary>
    [Required, MaxLength(20)]
    public string ComuneCode { get; set; } = string.Empty;

    /// <summary>
    /// The host org that invited the supplier as one of its trusted suppliers (bug #977); <c>null</c> for an admin invite.
    /// Accepting the invite adds the <see cref="TrustedSupplier"/> relation of this org, also for a supplier that is
    /// already registered.
    /// </summary>
    public Guid? InvitedByOrgId { get; set; }

    /// <summary>
    /// The properties of <see cref="InvitedByOrgId"/> the supplier may work on, copied to
    /// <see cref="TrustedSupplier.AssignedPropertyIds"/> (null or empty = all of them).
    /// </summary>
    public List<Guid>? AssignedPropertyIds { get; set; }

    /// <summary>JSON array of category codes; nullable when invite has no category filter.</summary>
    [Column(TypeName = "jsonb")]
    public string? CategoriesJson { get; set; }

    [MaxLength(1000)]
    public string? Message { get; set; }

    public bool IsUsed { get; set; }

    /// <summary>
    /// UTC moment an admin revoked the invite (SU-12): its link no longer works. The row is kept, so the invites list
    /// and the audit trail still show it.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
