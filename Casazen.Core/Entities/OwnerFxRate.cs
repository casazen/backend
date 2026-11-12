using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// An exchange rate the agency entered for the owner accounting (E06/F05/US02/T02): one unit of <see cref="BaseCurrency"/> is <see cref="Rate"/> units of
/// <see cref="QuoteCurrency"/> on <see cref="RateDate"/>, with where it comes from (a reference rate). CasaZen fetches no rate: a source in another
/// currency without a rate of its day (or of the 7 days before) is a readiness issue and is not posted.
/// </summary>
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
