using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The counter of the fiscal numbers of one org and series (NF-83, ADR-019-R4, E06/F01/US02/T01): the last number given. Read and incremented only by
/// the numbering service, under a transaction-scoped advisory lock and in the transaction that saves the document, so a rolled-back issue leaves no gap.
/// The row is seeded lazily by the service at the first use of the series (above any number already used), never by the migration.
/// </summary>
[Table("FiscalNumberSequences")]
public class FiscalNumberSequence : ITenantOwned
{
    /// <summary>Unique (<see cref="OrgId"/>, <see cref="Series"/>).</summary>
    public const string OrgSeriesIndexName = "UIX_FiscalNumberSequences_OrgId_Series";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the issuer.</summary>
    public Guid OrgId { get; set; }

    [Required, MaxLength(FiscalDocument.SeriesMaxLength)]
    public string Series { get; set; } = string.Empty;

    /// <summary>The last number given in the series (0 before the first).</summary>
    public int LastNumber { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
