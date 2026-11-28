using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The daily receipt (PDF) of the Alloggiati transmissions of one Questura account (AW-R05, ADR-015-R2): one per org, account, day
/// and mode (unique index <see cref="AccountDayModeIndexName"/>), not one per booking. The PDF is kept in the private bucket under
/// <see cref="StorageKey"/> (never a public URL), checked by its <see cref="ContentHash"/>, and kept five years
/// (<see cref="RetainUntil"/>).
/// </summary>
[Table("AlloggiatiReceipts")]
public class AlloggiatiReceipt : ITenantOwned
{
    public const string AccountDayModeIndexName = "UIX_AlloggiatiReceipts_OrgId_AccountKey_ReceiptDate_Mode";

    public const int StorageKeyMaxLength = 500;

    public const int ContentHashLength = 64;

    public const int RetentionYears = 5;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid AccountKey { get; set; }

    public DateTime ReceiptDate { get; set; }

    public AlloggiatiMode Mode { get; set; }

    [Required]
    [MaxLength(StorageKeyMaxLength)]
    public string StorageKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(ContentHashLength)]
    public string ContentHash { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime RetrievedAt { get; set; }

    public DateTime RetainUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
