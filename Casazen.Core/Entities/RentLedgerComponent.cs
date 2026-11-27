using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

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

    public DateOnly CompetenceStart { get; set; }

    public DateOnly CompetenceEnd { get; set; }

    public Guid? TenantSettlementId { get; set; }

    public Guid? SourceId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public RentLedgerEntry RentLedgerEntry { get; set; } = null!;

    public Org Org { get; set; } = null!;
}
