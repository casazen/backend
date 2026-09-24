using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Admin-generated supplier invite (US-022 / #292, AC3). The signup link carries a random token
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

    [Required, MaxLength(20)]
    public string ComuneCode { get; set; } = string.Empty;

    /// <summary>JSON array of category codes; nullable when invite has no category filter.</summary>
    [Column(TypeName = "jsonb")]
    public string? CategoriesJson { get; set; }

    [MaxLength(1000)]
    public string? Message { get; set; }

    public bool IsUsed { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
