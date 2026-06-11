using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("PlatformBillingMetrics")]
public class PlatformBillingMetrics
{
    [Key]
    public int Id { get; set; }

    public int CalendarYear { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal EuB2cCrossBorderRevenue { get; set; }

    public bool OssThresholdReached { get; set; }

    public DateTime? OssSwitchoverAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
