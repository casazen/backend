using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("CancellationPolicies")]
[Index(nameof(Slug), IsUnique = true, Name = "UIX_CancellationPolicies_Slug")]
public class CancellationPolicy
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Stable programmatic identifier (e.g. "ampia", "intermedia"). Never shown to guests; used
    /// by application code to look up a policy without relying on translated display names (PC-02).
    /// Callers that omit it still get a unique value: the column default is not <c>""</c>, and an
    /// empty string would collide on <c>UIX_CancellationPolicies_Slug</c>.
    /// </summary>
    [Required, MaxLength(50)]
    public string Slug { get; set; } = $"p-{Guid.NewGuid():N}";

    [Required, MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    public int FullRefundHours { get; set; }

    [Precision(18, 2)]
    public decimal PartialRefundPercent { get; set; }

    public int PartialRefundHours { get; set; }

    public int GraceBookingDaysBeforeCheckin { get; set; }

    public int GraceWindowHours { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Property> Properties { get; set; } = new List<Property>();
}
