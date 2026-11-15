using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One flat an Alloggiati receipt covers (E20/F02/US01/T01, NF-43, NF-47, decision O3). An agency with one police account («Gestione Appartamenti»
/// profile) gets ONE receipt for the day for several flats, so a <see cref="ManualComplianceProof"/> of the kind
/// <see cref="ManualProofKind.AlloggiatiReceipt"/> names, in this table, every flat of the organization it is about: the stays of those flats that
/// arrived on its day are the ones it proves (the light of a stay of another flat does not turn green with it).
/// </summary>
/// <remarks>
/// <para>Every proof has its own flat here: the flat it was filed under (<see cref="ManualComplianceProof.PropertyId"/>) is always one of the rows, and a
/// proof filed before the table existed got its single row from the migration. The pair (<see cref="ProofId"/>, <see cref="PropertyId"/>) is unique.
/// The reads (<c>ManualProofCoverage</c>) still count the flat of the proof itself, so a row written without a line here proves its own flat.</para>
/// <para>The row holds no guest data and the content of the PDF is never read: it is the host's word, checked against the org and the reach of the
/// person who uploaded it, of which flats the file is about. It goes with its proof and with its flat (cascade).</para>
/// </remarks>
[Table("ManualComplianceProofProperties")]
[Index(nameof(ProofId), nameof(PropertyId), IsUnique = true)]
[Index(nameof(OrgId), nameof(PropertyId))]
public class ManualComplianceProofProperty : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the proof (TN-2).</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Proof))]
    public Guid ProofId { get; set; }
    public virtual ManualComplianceProof Proof { get; set; } = null!;

    /// <summary>The flat the receipt covers.</summary>
    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;
}
