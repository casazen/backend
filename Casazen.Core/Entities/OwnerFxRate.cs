using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("OwnerFxRates")]
public class OwnerFxRate : ITenantOwned
{
    public const string RateIndexName = "UIX_OwnerFxRates_OrgId_Pair_Date";
    public const int CurrencyMaxLength = 3;
    public const int SourceMaxLength = 100;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string BaseCurrency { get; set; } = string.Empty;

    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string QuoteCurrency { get; set; } = string.Empty;

    public DateOnly RateDate { get; set; }

    [Precision(18, 8)]
    public decimal Rate { get; set; }

    [Required]
    [MaxLength(SourceMaxLength)]
    public string Source { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
