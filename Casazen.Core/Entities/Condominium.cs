using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One condominium the owner pays (CondoLedger <c>houses</c>). Amounts are the owner's quota, already assigned
/// by the administrator: there is no millesimi engine.
/// </summary>
[Table("Condominiums")]
public class Condominium : ITenantOwned
{
    public const int NameMaxLength = 200;
    public const int AdministratorNameMaxLength = 200;
    public const int AdministratorEmailMaxLength = 200;
    public const int AdministratorPhoneMaxLength = 50;
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(AdministratorNameMaxLength)]
    public string? AdministratorName { get; set; }

    [MaxLength(AdministratorEmailMaxLength)]
    public string? AdministratorEmail { get; set; }

    [MaxLength(AdministratorPhoneMaxLength)]
    public string? AdministratorPhone { get; set; }

    /// <summary>First month of the fiscal year, 1-12. CondoLedger defaults to June.</summary>
    public int FiscalStartMonth { get; set; } = 6;

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Org Org { get; set; } = null!;

    public ICollection<CondominiumUnit> Units { get; set; } = [];

    public ICollection<CondominiumPeriod> Periods { get; set; } = [];
}
