using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Wire transfer batch (ADR-017): one SEPA pain.001 or Swiss Payment Standards file per currency and debit account,
/// containing wire transfers to property owners for published statements. Each statement appears in exactly one batch.
/// Files are validated against official ISO 20022 XSD. IBANs are validated with check digits before inclusion.
/// </summary>
[Table("WireBatches")]
public class WireBatch : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>
    /// Currency of the batch (ISO 4217 code, e.g., "EUR", "CHF"). One batch per currency and debit account.
    /// </summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Debit account (IBAN) from which the transfers will be sent. One batch per currency and debit account.
    /// </summary>
    [Required]
    [MaxLength(34)]
    public string DebitAccount { get; set; } = string.Empty;

    /// <summary>
    /// Total amount of all wire transfers in the batch. Sum of statement NetPayable amounts, rounded to 2 decimals.
    /// </summary>
    [Precision(18, 2)]
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Number of wire transfers in the batch (one per owner, aggregated when the same owner has multiple statements).
    /// </summary>
    public int TransferCount { get; set; }

    /// <summary>
    /// Batch lifecycle status: Generated (created), Downloaded (file retrieved), Paid (confirmed as executed).
    /// </summary>
    public WireBatchStatus Status { get; set; } = WireBatchStatus.Generated;

    /// <summary>When the batch was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the file was downloaded. Null while Status != Downloaded and Status != Paid.</summary>
    public DateTime? DownloadedAt { get; set; }

    /// <summary>When the batch was confirmed as paid. Null while Status != Paid.</summary>
    public DateTime? PaidAt { get; set; }
}
