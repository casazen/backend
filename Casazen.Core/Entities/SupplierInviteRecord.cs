using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("SupplierInviteRecords")]
public class SupplierInviteRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? TokenHash { get; set; }

    [Required, MaxLength(20)]
    public string ComuneCode { get; set; } = string.Empty;

    public Guid? InvitedByOrgId { get; set; }

    public bool? HostOriginAtInvitation { get; set; }

    public Guid? IdentifiedSupplierOrgId { get; set; }

    [ConcurrencyCheck]
    public string? IdentityReviewJson { get; set; }

    public List<Guid>? AssignedPropertyIds { get; set; }

    [Column(TypeName = "jsonb")]
    public string? CategoriesJson { get; set; }

    [MaxLength(1000)]
    public string? Message { get; set; }

    public bool IsUsed { get; set; }

    [ConcurrencyCheck]
    public DateTime? RevokedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
