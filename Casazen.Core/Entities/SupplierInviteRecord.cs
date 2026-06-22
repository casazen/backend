using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Admin-generated supplier invite. Token included in the signup link and validated during
/// <c>POST /api/suppliers/register</c> (US-022 / #292, AC3).
/// </summary>
[Table("SupplierInviteRecords")]
public class SupplierInviteRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

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
