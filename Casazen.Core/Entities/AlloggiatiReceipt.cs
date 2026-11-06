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
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="AccountKey"/>, <see cref="ReceiptDate"/>, <see cref="Mode"/>).</summary>
    public const string AccountDayModeIndexName = "UIX_AlloggiatiReceipts_OrgId_AccountKey_ReceiptDate_Mode";

    /// <summary>Longest <see cref="StorageKey"/>.</summary>
    public const int StorageKeyMaxLength = 500;

    /// <summary>Length of a SHA-256 in hexadecimal (<see cref="ContentHash"/>).</summary>
    public const int ContentHashLength = 64;

    /// <summary>How long a receipt is kept (AW-R05).</summary>
    public const int RetentionYears = 5;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the account.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The Questura account of the receipt (<see cref="PropertyQuesturaCredentials.AccountKey"/>).</summary>
    public Guid AccountKey { get; set; }

    /// <summary>The Europe/Rome day of the transmissions, stored like the booking dates (midnight UTC of that day).</summary>
    public DateTime ReceiptDate { get; set; }

    /// <summary>Simulated or live: a simulated receipt never stands for a real one.</summary>
    public AlloggiatiMode Mode { get; set; }

    /// <summary>The key of the PDF in the private bucket.</summary>
    [Required]
    [MaxLength(StorageKeyMaxLength)]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>SHA-256 (hexadecimal) of the PDF as stored.</summary>
    [Required]
    [MaxLength(ContentHashLength)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Size of the PDF in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>When the PDF was retrieved from the service and stored (UTC).</summary>
    public DateTime RetrievedAt { get; set; }

    /// <summary>Until when the receipt is kept (UTC): <see cref="RetentionYears"/> years.</summary>
    public DateTime RetainUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
