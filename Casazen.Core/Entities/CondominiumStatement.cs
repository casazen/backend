using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A preventivo or consuntivo. A correction inserts a new revision and marks the previous one
/// <see cref="CondominiumStatementStatus.Superseded"/>. Amounts on the previous row are never rewritten.
/// </summary>
[Table("CondominiumStatements")]
public class CondominiumStatement : ITenantOwned
{
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid CondominiumPeriodId { get; set; }

    public CondominiumStatementKind Kind { get; set; }

    public int RevisionNumber { get; set; }

    public CondominiumStatementStatus Status { get; set; } = CondominiumStatementStatus.Confirmed;

    public Guid? DocumentId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public CondominiumPeriod Period { get; set; } = null!;

    public Org Org { get; set; } = null!;

    public ICollection<CondominiumExpenseLine> Lines { get; set; } = [];
}
