using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A length-of-stay discount of a property (NF-35, ADR-028-R2): from <see cref="MinNights"/> nights the lodging costs
/// <see cref="PercentOff"/> percent less. A property has at most <see cref="MaxPerProperty"/> tiers, each with its own minimum of nights; the
/// tier with the most nights that the stay reaches is the one that applies (<c>StayPricing</c>). A property with no row gives no discount, which
/// is every property that existed before the table.
/// </summary>
/// <remarks>Rows follow their property (cascade) and carry its <see cref="OrgId"/> (TN-2).</remarks>
[Table("PropertyStayDiscounts")]
public class PropertyStayDiscount : ITenantOwned
{
    /// <summary>At most this many tiers a property can have.</summary>
    public const int MaxPerProperty = 5;

    /// <summary>Fewest nights a tier can ask for: below it there is no length of stay to reward.</summary>
    public const int MinNightsLowest = 2;

    /// <summary>Most nights a tier can ask for: a year.</summary>
    public const int MinNightsHighest = 365;

    /// <summary>Largest discount: the whole lodging.</summary>
    public const decimal MaxPercentOff = 100m;

    /// <summary>Decimals kept for the percentage (the column is <c>numeric(5,2)</c>).</summary>
    public const int PercentScale = 2;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The fewest nights of a stay this tier applies to, <see cref="MinNightsLowest"/> to <see cref="MinNightsHighest"/>; unique for the property.</summary>
    public int MinNights { get; set; }

    /// <summary>The share of the lodging taken off, above 0 and at most <see cref="MaxPercentOff"/>, two decimals.</summary>
    [Precision(5, 2)]
    public decimal PercentOff { get; set; }
}
