using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A proof the host uploads when CasaZen cannot download it itself (E03/F02/US01/T01, NF-47, NF-52, ADR-015): the receipt of the
/// Alloggiati Web communication of a day, the proof of the ISTAT communication of a period, the tourist tax declaration or payment
/// receipt of a period. It is the evidence that lets the compliance traffic light turn green (<c>ComplianceLightRules</c>): without a row
/// here, or an <see cref="AlloggiatiReceipt"/>, nothing is green.
/// </summary>
/// <remarks>
/// <para>The file is stored encrypted in the private bucket under <see cref="FileKey"/> (never a public URL), like the other compliance
/// proofs (ADR-015, ADR-030-R3), and kept <see cref="RetentionYears"/> years from <see cref="UploadedAt"/> (<see cref="RetainUntil"/>).
/// The row holds no guest data: the property, the kind, the day, the key of the file and who uploaded it.</para>
/// <para>Append only: a wrong upload is not edited, a new one is made. The pair (<see cref="Kind"/>, <see cref="Day"/>) of a property may
/// hold several rows (a receipt uploaded twice, a payment in instalments).</para>
/// </remarks>
[Table("ManualComplianceProofs")]
[Index(nameof(OrgId), nameof(PropertyId), nameof(Kind), nameof(Day))]
public class ManualComplianceProof : ITenantOwned
{
    public const int FileKeyMaxLength = 500;

    public const int ContentTypeMaxLength = 100;

    public const int UserIdMaxLength = 255;

    public const int RetentionYears = 5;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>
    /// The flats of the organization the proof covers (E20/F02/US01/T01, decision O3): for an Alloggiati receipt one or more, the flat it was filed
    /// under included; for every other kind just that flat. Not loaded unless asked for.
    /// </summary>
    public virtual ICollection<ManualComplianceProofProperty> CoveredProperties { get; set; } = [];

    public ManualProofKind Kind { get; set; }

    /// <summary>
    /// What the proof is about, a Europe/Rome calendar date: the day of the Alloggiati communication (the arrival day of the stays it covers)
    /// or the first day of the ISTAT or tax period.
    /// </summary>
    public DateOnly Day { get; set; }

    [Required, MaxLength(FileKeyMaxLength)]
    public string FileKey { get; set; } = string.Empty;

    [Required, MaxLength(ContentTypeMaxLength)]
    public string ContentType { get; set; } = string.Empty;

    [Required, MaxLength(UserIdMaxLength)]
    public string UploadedByUserId { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public DateTime RetainUntil => UploadedAt.AddYears(RetentionYears);
}

public enum ManualProofKind
{
    AlloggiatiReceipt = 1,

    TouristTaxDeclaration = 2,

    TouristTaxPayment = 3,

    /// <summary>
    /// The proof of the ISTAT communication of a period. Added with the status engine (E03/F02/US01/T03): the regional ISTAT service is
    /// postponed (plan of 2026-11-05), so a manual proof is the only evidence an ISTAT period can have.
    /// </summary>
    IstatProof = 4,
}
