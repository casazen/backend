using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Casazen.Core.Pricing;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Seasonal price suggestion of one stay date of a property (PC-15): one row per (property, date), regenerated in place by
/// every computation (never a growing history). A proposal shown to the host: quotes and bookings keep using the
/// property's nightly rate.
/// </summary>
[Table("SeasonalPriceSuggestions")]
public class SeasonalPriceSuggestion : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PropertyId { get; set; }

    public Guid OrgId { get; set; }

    public DateOnly StayDate { get; set; }

    [Precision(18, 2)]
    public decimal BasePrice { get; set; }

    [Precision(18, 2)]
    public decimal SuggestedPrice { get; set; }

    [Precision(4, 2)]
    public decimal Multiplier { get; set; }

    [MaxLength(20)]
    public SeasonalPriceRule Rule { get; set; }

    [MaxLength(30)]
    public ItalianHoliday? Holiday { get; set; }

    public DateTime ComputedAt { get; set; }

    /// <summary>
    /// Nightly price the host confirmed for this date (PO 2026-10-08). Null until the host applies the proposal
    /// (optionally after editing it). Quotes and bookings use this when set, otherwise the property nightly rate.
    /// </summary>
    [Precision(18, 2)]
    public decimal? AppliedPrice { get; set; }

    public DateTime? AppliedAt { get; set; }
}
