using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Wire transfer batch (ADR-017, E16/F01/US01/T01, E16/F01/US02/T01): one bank file per currency, built from the <b>confirmed payouts</b> of the owners
/// (<see cref="OwnerPayout"/>, the net after the withholding) that are not in another batch yet; the payouts of a batch are the rows of
/// <see cref="WireBatchPayout"/>. CHF is the Swiss <c>pain.001.001.09.ch.03</c> file, EUR the ISO 20022 <c>pain.001.001.09</c> file with the rules of the SEPA
/// credit transfer, both with structured addresses (<see cref="MessageFormat"/>). A file is validated against the official schema of its format before the batch
/// is saved, and is never produced without that validation.
/// </summary>
/// <remarks>
/// <b>Privacy.</b> The file holds the IBAN, the name and the address of every owner, and the debit account is the IBAN of the agency: both
/// <see cref="XmlContent"/> and <see cref="DebitAccount"/> are encrypted at rest (<c>EncryptedColumns</c>, purpose <c>Casazen.WireBatch.File</c>), so the
/// columns have no length (the length of the IBAN is checked on input) and nothing searches in them. A batch is listed without either of them.
/// </remarks>
[Table("WireBatches")]
public class WireBatch : ITenantOwned
{
    public const int MessageFormatMaxLength = 40;

    public const int DebitAccountMaxLength = 34;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    [Required]
    public string DebitAccount { get; set; } = string.Empty;

    [Precision(18, 2)]
    public decimal TotalAmount { get; set; }

    public int TransferCount { get; set; }

    public WireBatchStatus Status { get; set; } = WireBatchStatus.Generated;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DownloadedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateOnly? ExecutionDate { get; set; }

    [MaxLength(MessageFormatMaxLength)]
    public string? MessageFormat { get; set; }

    [MaxLength(-1)]
    public string? XmlContent { get; set; }
}
