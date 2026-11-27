using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("PropertyStayDiscounts")]
public class PropertyStayDiscount : ITenantOwned
{
    public const int MaxPerProperty = 5;

    public const int MinNightsLowest = 2;

    public const int MinNightsHighest = 365;

    public const decimal MaxPercentOff = 100m;

    public const int PercentScale = 2;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public int MinNights { get; set; }

    [Precision(5, 2)]
    public decimal PercentOff { get; set; }
}
