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

    /// <summary>Tenant of the row, copied from the property (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>Stay date (the night starting that day), a calendar date without time.</summary>
    public DateOnly StayDate { get; set; }

    /// <summary>The property's nightly rate used as base.</summary>
    [Precision(18, 2)]
    public decimal BasePrice { get; set; }

    /// <summary>Base price x multiplier, rounded to the cent.</summary>
    [Precision(18, 2)]
    public decimal SuggestedPrice { get; set; }

    /// <summary>Multiplier of the applied rule (1 when none applies).</summary>
    [Precision(4, 2)]
    public decimal Multiplier { get; set; }

    /// <summary>The rule applied.</summary>
    [MaxLength(20)]
    public SeasonalPriceRule Rule { get; set; }

    /// <summary>The national holiday when <see cref="Rule"/> is <see cref="SeasonalPriceRule.Holiday"/>.</summary>
    [MaxLength(30)]
    public ItalianHoliday? Holiday { get; set; }

    /// <summary>UTC instant of the computation that wrote the row.</summary>
    public DateTime ComputedAt { get; set; }
}
