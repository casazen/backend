using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Wire transfer batch (ADR-017, E16/F01/US01/T01): one bank file per currency, built from the <b>confirmed payouts</b> of the owners
/// (<see cref="OwnerPayout"/>, the net after the withholding) that are not in another batch yet; the payouts of a batch are the rows of
/// <see cref="WireBatchPayout"/>. CHF is the Swiss <c>pain.001.001.09.ch.03</c> file with structured addresses, EUR keeps the SEPA <c>pain.001.001.03</c> file
/// until its own change (<see cref="MessageFormat"/>). A file is validated against the official schema of its format before the batch is saved, and
/// is never produced without that validation.
/// </summary>
/// <remarks>
/// <b>Privacy.</b> The file holds the IBAN, the name and the address of every owner, and the debit account is the IBAN of the agency: both
/// <see cref="XmlContent"/> and <see cref="DebitAccount"/> are encrypted at rest (<c>EncryptedColumns</c>, purpose <c>Casazen.WireBatch.File</c>), so the
/// columns have no length (the length of the IBAN is checked on input) and nothing searches in them. A batch is listed without either of them.
/// </remarks>
[Table("WireBatches")]
public class WireBatch : ITenantOwned
{
    /// <summary>The longest <see cref="MessageFormat"/>.</summary>
    public const int MessageFormatMaxLength = 40;

    /// <summary>The longest IBAN (ISO 13616), checked on input: the encrypted <see cref="DebitAccount"/> column has no length.</summary>
    public const int DebitAccountMaxLength = 34;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>
    /// Currency of the batch (ISO 4217 code, e.g., "EUR", "CHF"). One batch per currency: the payouts of a batch are all in it.
    /// </summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Debit account (IBAN) from which the transfers will be sent: the IBAN of the issuer profile of the org when the batch was made. <b>Encrypted at
    /// rest</b> (no length, <see cref="DebitAccountMaxLength"/> is checked on input); never listed, never logged.
    /// </summary>
    [Required]
    public string DebitAccount { get; set; } = string.Empty;

    /// <summary>
    /// Total amount of all wire transfers in the batch: the sum of the net amounts of its payouts (<see cref="WireBatchPayout.Amount"/>), with two decimals.
    /// </summary>
    [Precision(18, 2)]
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Number of wire transfers in the batch: one for each payout (a payout is a single transfer to its owner, never added to another one).
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

    /// <summary>
    /// The execution date the file asks the bank for (the <c>ReqdExctnDt</c> of its payment); <c>null</c> for a batch of before E16/F01/US01/T01, which
    /// asked for the day after its creation.
    /// </summary>
    public DateOnly? ExecutionDate { get; set; }

    /// <summary>
    /// The message the file is (<c>pain.001.001.09.ch.03</c> for CHF, <c>pain.001.001.03</c> for EUR, <see cref="WireMessageFormats"/>); <c>null</c> for a batch of
    /// before E16/F01/US01/T01, which was a <c>pain.001.001.03</c> file in both currencies.
    /// </summary>
    [MaxLength(MessageFormatMaxLength)]
    public string? MessageFormat { get; set; }

    /// <summary>
    /// The generated XML file content. Stored to ensure the exact file downloaded matches what was generated. <b>Encrypted at rest</b>: it holds the IBAN, the
    /// name and the address of every owner. The column keeps its type (<c>text</c>), the converter writes a longer text in it.
    /// </summary>
    [MaxLength(-1)]
    public string? XmlContent { get; set; }
}
