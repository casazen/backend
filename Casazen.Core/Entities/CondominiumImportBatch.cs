using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One <c>condo-export-v1</c> copy waiting in staging. Confirming a house with a verified property is what writes the book.
/// The same hash is stored once.
/// </summary>
[Table("CondominiumImportBatches")]
public class CondominiumImportBatch : ITenantOwned
{
    public const int ContentHashMaxLength = 80;
    public const int FormatMaxLength = 40;
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(ContentHashMaxLength)]
    public string ContentHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(FormatMaxLength)]
    public string Format { get; set; } = string.Empty;

    public CondominiumImportStatus Status { get; set; } = CondominiumImportStatus.Staged;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public Org Org { get; set; } = null!;

    public ICollection<CondominiumImportHouse> Houses { get; set; } = [];
}
