using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Links a CasaZen property to a condominium. <see cref="Millesimi"/> is only what the administrator already wrote;
/// nothing is calculated from it.
/// </summary>
[Table("CondominiumUnits")]
public class CondominiumUnit : ITenantOwned
{
    public const int UnitReferenceMaxLength = 100;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid CondominiumId { get; set; }

    public Guid PropertyId { get; set; }

    [MaxLength(UnitReferenceMaxLength)]
    public string? UnitReference { get; set; }

    /// <summary>Informational thousandths. Never used to split an amount.</summary>
    [Column(TypeName = "decimal(8,2)")]
    public decimal? Millesimi { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Condominium Condominium { get; set; } = null!;

    public Property Property { get; set; } = null!;

    public Org Org { get; set; } = null!;
}
