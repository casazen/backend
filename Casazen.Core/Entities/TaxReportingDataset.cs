using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The annual data of a tax report (E06/F05/US02/T03, AC-NF110-5): the Certificazione Unica of the withholding of a year, per owner, built from the
/// applicable evidence of the payouts paid in the year, with its filing state. Generating it is never filing it: it is submitted only through a
/// controlled manual handoff, then accepted (with the receipt) or rejected.
/// </summary>
[Table("TaxReportingDatasets")]
public class TaxReportingDataset : ITenantOwned
{
    public const string KindYearIndexName = "UIX_TaxReportingDatasets_OrgId_Kind_Year";
    public const int KindMaxLength = 16;
    public const int ReferenceMaxLength = 200;
    public const int HashLength = 64;
    public const int UserIdMaxLength = 255;

    /// <summary>The annual Certificazione Unica of the withholding.</summary>
    public const string CertificazioneUnica = "cu";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(KindMaxLength)]
    public string Kind { get; set; } = CertificazioneUnica;

    public int Year { get; set; }

    [Precision(18, 2)]
    public decimal TotalGrossBase { get; set; }

    [Precision(18, 2)]
    public decimal TotalWithheld { get; set; }

    public int EvidenceCount { get; set; }

    /// <summary>SHA-256 of the evidence ids and amounts the data was built from.</summary>
    [Required]
    [MaxLength(HashLength)]
    public string EvidenceSha256 { get; set; } = string.Empty;

    /// <summary>The data per owner (JSON): owner id, gross base, withheld, evidence count.</summary>
    [Required]
    public string PayloadJson { get; set; } = "[]";

    public TaxFilingState FilingState { get; set; } = TaxFilingState.Generated;

    /// <summary>The reference of the controlled manual handoff (who sent it, through which channel).</summary>
    [MaxLength(ReferenceMaxLength)]
    public string? HandoffReference { get; set; }

    /// <summary>The receipt of the authority once accepted.</summary>
    [MaxLength(ReferenceMaxLength)]
    public string? ReceiptReference { get; set; }

    [MaxLength(ReferenceMaxLength)]
    public string? RejectionReason { get; set; }

    public DateTime GeneratedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? GeneratedByUserId { get; set; }

    public DateTime? FilingUpdatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? FilingUpdatedByUserId { get; set; }

    /// <summary>The PostgreSQL <c>xmin</c> of the row.</summary>
    public uint RowVersion { get; set; }
}
