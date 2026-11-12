using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One part of a <see cref="RentLedgerEntry"/> (CondoLedger phase 1): rent, condominium advance, settlement or another
/// charge. The installment's <see cref="RentLedgerEntry.AmountDue"/> is the sum of these amounts.
/// </summary>
[Table("RentLedgerComponents")]
public class RentLedgerComponent : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid RentLedgerEntryId { get; set; }

    public RentLedgerComponentKind Kind { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>First day of the period this component covers (a date, no time).</summary>
    public DateOnly CompetenceStart { get; set; }

    /// <summary>Last day of the period this component covers, included.</summary>
    public DateOnly CompetenceEnd { get; set; }

    /// <summary>The tenant settlement this component came from, once that module exists. No foreign key in phase 1.</summary>
    public Guid? TenantSettlementId { get; set; }

    /// <summary>Where the amount was taken from (a document, a budget line). No foreign key in phase 1.</summary>
    public Guid? SourceId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public RentLedgerEntry RentLedgerEntry { get; set; } = null!;

    public Org Org { get; set; } = null!;
}
