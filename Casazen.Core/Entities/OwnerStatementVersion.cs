using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A version of the statement of an owner for a month (E06/F05/US02/T02, NF-110, AC-NF110-1, AC-NF110-2): the totals of the ledger lines of the month
/// posted up to <see cref="CutoffAt"/>, with the hash of those lines, so every amount is reproducible from the ledger. A change after the cut-off makes
/// the month dirty and needs a new version; a published version never changes (its file and hash stay), a later one supersedes it.
/// </summary>
[Table("OwnerStatementVersions")]
public class OwnerStatementVersion : ITenantOwned
{
    public const string VersionIndexName = "UIX_OwnerStatementVersions_OrgId_Owner_Period_Version";
    public const int CurrencyMaxLength = 3;
    public const int HashLength = 64;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public int PeriodYear { get; set; }

    public int PeriodMonth { get; set; }

    /// <summary>1 for the first version of the month, then +1.</summary>
    public int VersionNumber { get; set; }

    public OwnerStatementVersionStatus Status { get; set; } = OwnerStatementVersionStatus.Draft;

    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>The ledger cut-off: the version is made of the lines of the month posted up to this instant.</summary>
    public DateTime CutoffAt { get; set; }

    /// <summary>The balance of the lines of the months before, posted up to the cut-off.</summary>
    [Precision(18, 2)]
    public decimal OpeningBalance { get; set; }

    [Precision(18, 2)]
    public decimal Revenue { get; set; }

    [Precision(18, 2)]
    public decimal ChannelFees { get; set; }

    [Precision(18, 2)]
    public decimal Commission { get; set; }

    /// <summary>The charges and the expenses.</summary>
    [Precision(18, 2)]
    public decimal Charges { get; set; }

    /// <summary>The payments to and from the owner.</summary>
    [Precision(18, 2)]
    public decimal Payments { get; set; }

    [Precision(18, 2)]
    public decimal Corrections { get; set; }

    /// <summary>What the agency owes the owner at the end of the month: the opening balance plus the lines of the month.</summary>
    [Precision(18, 2)]
    public decimal ClosingBalance { get; set; }

    public int EntryCount { get; set; }

    /// <summary>SHA-256 of the ids and amounts of the lines of the version, in order: the proof they are the ones it was made of.</summary>
    [Required]
    [MaxLength(HashLength)]
    public string EntriesSha256 { get; set; } = string.Empty;

    /// <summary>The readiness issues found at the generation (JSON array of codes and ids), <c>null</c> without any.</summary>
    public string? IssuesJson { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ApprovedByUserId { get; set; }

    public DateTime? PublishedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? PublishedByUserId { get; set; }

    /// <summary>The published file: the JSON of the version and of its lines, as the owner received it.</summary>
    public string? PublishedSnapshot { get; set; }

    [MaxLength(HashLength)]
    public string? PublishedSha256 { get; set; }

    public Guid? SupersedesVersionId { get; set; }

    public Guid? SupersededByVersionId { get; set; }

    public DateTime? SupersededAt { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    /// <summary>The PostgreSQL <c>xmin</c> of the row: two writers that read the same version cannot both change it.</summary>
    public uint RowVersion { get; set; }
}
