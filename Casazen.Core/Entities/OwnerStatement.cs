using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Monthly owner statement (ADR-017). One statement per property owner per property per month.
/// For co-ownership, each co-owner gets their own statement (split by percentage per ADR-017-R3/R4).
/// Net amount is computed from line items and rounded ONCE to 2 decimals with banker's rounding (ADR-017-R2).
/// </summary>
[Table("OwnerStatements")]
public class OwnerStatement : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the property; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The property owner this statement is for.</summary>
    public Guid PropertyOwnerId { get; set; }
    public virtual PropertyOwner PropertyOwner { get; set; } = null!;

    /// <summary>The property this statement covers.</summary>
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>
    /// Statement period: month (1-12). Together with Year identifies the period.
    /// </summary>
    public int Month { get; set; }

    /// <summary>
    /// Statement period: year (e.g., 2026). Together with Month identifies the period.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Currency of the statement (ISO 4217 code, e.g., "EUR", "USD"). Must match the org's base currency.
    /// </summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Statement lifecycle status: Draft (editable), Approved (locked), Published (sent to owner).
    /// Only Draft statements may be edited (e.g., adding OTA revenue from CSV, adjustments).
    /// </summary>
    public StatementStatus Status { get; set; } = StatementStatus.Draft;

    /// <summary>
    /// Total gross revenue (sum of line items' GrossRevenue). Full precision, not rounded (ADR-017-R1).
    /// Computed when the statement is generated or recalculated after manual changes.
    /// </summary>
    [Precision(18, 2)]
    public decimal TotalRevenue { get; set; }

    /// <summary>
    /// Total commission charged (sum of line items' Commission). Full precision, not rounded (ADR-017-R1).
    /// </summary>
    [Precision(18, 2)]
    public decimal Commission { get; set; }

    /// <summary>
    /// Total expenses charged to the owner (sum of line items' Expenses). Full precision, not rounded (ADR-017-R1).
    /// </summary>
    [Precision(18, 2)]
    public decimal Expenses { get; set; }

    /// <summary>
    /// Total withholding tax (sum of line items' Withholding). Full precision, not rounded (ADR-017-R1).
    /// </summary>
    [Precision(18, 2)]
    public decimal WithholdingTax { get; set; }

    /// <summary>
    /// Net payable to the owner (TotalRevenue - Commission - Expenses - WithholdingTax).
    /// Rounded ONCE to 2 decimals using banker's rounding (MidpointRounding.ToEven, ADR-017-R2).
    /// For co-owners, this is the owner's share after split (ADR-017-R3/R4).
    /// </summary>
    [Precision(18, 2)]
    public decimal NetPayable { get; set; }

    /// <summary>
    /// For co-ownership: the owner's ownership percentage at the time the statement was generated (e.g., 33.33).
    /// 100.00 for sole ownership. Used to document the split ratio in the published statement.
    /// </summary>
    [Precision(5, 2)]
    public decimal OwnershipPercent { get; set; } = 100.00m;

    /// <summary>
    /// When the statement was published (sent to the owner). Null while Status != Published.
    /// Once published, the statement is immutable (corrections via adjustment rows in next month, ADR-017-R5).
    /// </summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>When this statement was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this statement was last updated (UTC). Updated on recalculation or status change.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Navigation property: line items for this statement.</summary>
    public virtual ICollection<StatementLine> Lines { get; set; } = new List<StatementLine>();
}
