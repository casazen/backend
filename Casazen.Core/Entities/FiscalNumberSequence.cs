using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The counter of the fiscal numbers of one org, issuer and series (NF-83, NF-116, ADR-019-R4, E06/F01/US02/T01, E17/F01/US01/T01): the last number given. The
/// issuer is the profile the documents are issued on (the organization, or an owner who issues with its own series, the sezionale). Read and incremented only
/// by the numbering service, under a transaction-scoped advisory lock and in the transaction that saves the document, so a rolled-back issue leaves no gap.
/// The row is seeded lazily by the service at the first use of the series (above any number already used), never by the migration.
/// </summary>
[Table("FiscalNumberSequences")]
public class FiscalNumberSequence : ITenantOwned
{
    public const string IssuerSeriesIndexName = "UIX_FiscalNumberSequences_OrgId_IssuerProfileId_Series";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid IssuerProfileId { get; set; }

    [Required, MaxLength(FiscalDocument.SeriesMaxLength)]
    public string Series { get; set; } = string.Empty;

    public int LastNumber { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
