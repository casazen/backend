using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One CondoLedger house in staging. Without a verified <see cref="PropertyId"/> it never changes a balance.
/// </summary>
[Table("CondominiumImportHouses")]
public class CondominiumImportHouse : ITenantOwned
{
    public const int SourceIdMaxLength = 80;
    public const int NameMaxLength = 200;
    public const int LocationMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BatchId { get; set; }

    [Required]
    [MaxLength(SourceIdMaxLength)]
    public string SourceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(LocationMaxLength)]
    public string? Location { get; set; }

    public int FiscalStartMonth { get; set; }

    /// <summary>Set when someone verifies which CasaZen property this house is. Until then the row stays staged.</summary>
    public Guid? PropertyId { get; set; }

    public Guid? CondominiumId { get; set; }

    public CondominiumImportStatus Status { get; set; } = CondominiumImportStatus.Staged;

    public CondominiumImportBatch Batch { get; set; } = null!;

    public Property? Property { get; set; }

    public Org Org { get; set; } = null!;

    public ICollection<CondominiumImportYear> Years { get; set; } = [];
}
