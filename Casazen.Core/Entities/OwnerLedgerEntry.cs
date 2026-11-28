using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One line of the append-only owner sub-ledger of an agency (E06/F05/US02/T02, NF-110, AC-NF110-1): what a source (a payment, a booking, a charge, a
/// paid wire, a correction) means for one owner, with the facts that reproduce the amount: the source and its document, the dates, the property, the
/// effective share, the original and the statement currency with the exchange rate, its source and date, the rule version. A posted line is never
/// changed or removed (<see cref="IAppendOnlyRecord"/>): a correction is a reversal (<see cref="ReversesEntryId"/>) and a replacement line.
/// </summary>
[Table("OwnerLedgerEntries")]
public class OwnerLedgerEntry : ITenantOwned, IAppendOnlyRecord
{
    public const string IdempotencyIndexName = "UIX_OwnerLedgerEntries_OrgId_IdempotencyKey";
    public const string ReversalIndexName = "UIX_OwnerLedgerEntries_ReversesEntryId";
    public const int KeyMaxLength = 200;
    public const int ReferenceMaxLength = 100;
    public const int TextMaxLength = 300;
    public const int AttachmentMaxLength = 500;
    public const int CurrencyMaxLength = 3;
    public const int CodeMaxLength = 32;
    public const int RuleVersionMaxLength = 80;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public Guid? PropertyId { get; set; }

    public OwnerLedgerEntryType EntryType { get; set; }

    public OwnerLedgerSourceType SourceType { get; set; }

    public Guid? SourceId { get; set; }

    [MaxLength(ReferenceMaxLength)]
    public string? SourceReference { get; set; }

    public Guid? SourceDocumentId { get; set; }

    [MaxLength(AttachmentMaxLength)]
    public string? AttachmentReference { get; set; }

    [Required]
    [MaxLength(KeyMaxLength)]
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateOnly ServiceDate { get; set; }

    public DateOnly RecognitionDate { get; set; }

    public DateOnly? PaymentDate { get; set; }

    public int PeriodYear { get; set; }

    public int PeriodMonth { get; set; }

    public Guid? PropertyOwnershipId { get; set; }

    [Precision(5, 2)]
    public decimal SharePercent { get; set; }

    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string OriginalCurrency { get; set; } = string.Empty;

    [Precision(18, 2)]
    public decimal SourceAmount { get; set; }

    [Precision(18, 2)]
    public decimal OriginalAmount { get; set; }

    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string StatementCurrency { get; set; } = string.Empty;

    [Precision(18, 8)]
    public decimal FxRate { get; set; } = 1m;

    [MaxLength(ReferenceMaxLength)]
    public string? FxSource { get; set; }

    public DateOnly? FxDate { get; set; }

    [MaxLength(CodeMaxLength)]
    public string? TaxCode { get; set; }

    [MaxLength(RuleVersionMaxLength)]
    public string? RuleVersion { get; set; }

    [Precision(18, 2)]
    public decimal Amount { get; set; }

    public Guid? ReversesEntryId { get; set; }

    [MaxLength(TextMaxLength)]
    public string? Description { get; set; }

    public DateTime PostedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? PostedByUserId { get; set; }
}
