using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("OwnerDocuments")]
public class OwnerDocument : ITenantOwned
{
    public const int TitleMaxLength = 160;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public Guid? PropertyId { get; set; }

    public OwnerDocumentType Type { get; set; } = OwnerDocumentType.Other;

    [Required]
    [MaxLength(TitleMaxLength)]
    public string Title { get; set; } = string.Empty;

    public DateTime? RevokedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? RevokedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
