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

    /// <summary>The owner the line belongs to.</summary>
    public Guid PropertyOwnerId { get; set; }

    /// <summary>The property of the source; <c>null</c> for a line of the owner as a whole (a correction without a property).</summary>
    public Guid? PropertyId { get; set; }

    public OwnerLedgerEntryType EntryType { get; set; }

    public OwnerLedgerSourceType SourceType { get; set; }

    /// <summary>The id of the source row (the payment, the booking, the charge, the wire batch).</summary>
    public Guid? SourceId { get; set; }

    /// <summary>A reference within the source: the booking of a payment, the month of a charge, the line of a wire batch.</summary>
    [MaxLength(ReferenceMaxLength)]
    public string? SourceReference { get; set; }

    /// <summary>The fiscal document of the source, when there is one.</summary>
    public Guid? SourceDocumentId { get; set; }

    /// <summary>Where the receipt or the invoice of a charge or a correction is kept.</summary>
    [MaxLength(AttachmentMaxLength)]
    public string? AttachmentReference { get; set; }

    /// <summary>
    /// Unique in the org: the source, the type, the owner and the amounts the line was computed with. Posting the same facts twice finds it; changed facts
    /// get a new key, and the line of the old key is reversed.
    /// </summary>
    [Required]
    [MaxLength(KeyMaxLength)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>When the service was rendered (the check-in of a stay, the day of a charge).</summary>
    public DateOnly ServiceDate { get; set; }

    /// <summary>When the line counts for the owner, by the recognition rule of the owner's profile; it decides the period.</summary>
    public DateOnly RecognitionDate { get; set; }

    /// <summary>When the money moved, for a payment.</summary>
    public DateOnly? PaymentDate { get; set; }

    public int PeriodYear { get; set; }

    public int PeriodMonth { get; set; }

    /// <summary>The ownership the share was read from, effective on the date of the source.</summary>
    public Guid? PropertyOwnershipId { get; set; }

    /// <summary>The share of the owner applied to the source, 100 for a line of the owner alone.</summary>
    [Precision(5, 2)]
    public decimal SharePercent { get; set; }

    /// <summary>The currency of the source.</summary>
    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string OriginalCurrency { get; set; } = string.Empty;

    /// <summary>The whole amount of the source in its currency, before the share.</summary>
    [Precision(18, 2)]
    public decimal SourceAmount { get; set; }

    /// <summary>The owner's part of the source in its currency (the shares of all the owners add up to <see cref="SourceAmount"/>), signed.</summary>
    [Precision(18, 2)]
    public decimal OriginalAmount { get; set; }

    /// <summary>The currency of the owner's statement (the settlement currency of the profile).</summary>
    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string StatementCurrency { get; set; } = string.Empty;

    /// <summary>The multiplier from the original to the statement currency: <c>Amount = round(OriginalAmount × FxRate, 2)</c>; 1 for the same currency.</summary>
    [Precision(18, 8)]
    public decimal FxRate { get; set; } = 1m;

    /// <summary>Where the rate comes from (the reference rate the agency entered).</summary>
    [MaxLength(ReferenceMaxLength)]
    public string? FxSource { get; set; }

    /// <summary>The day of the rate.</summary>
    public DateOnly? FxDate { get; set; }

    /// <summary>The tax code the rule set gives the line, when it has one.</summary>
    [MaxLength(CodeMaxLength)]
    public string? TaxCode { get; set; }

    /// <summary>The approved rule set the line was computed with (<c>{ruleCode}@{version}</c>); <c>null</c> when the owner had none (a readiness issue).</summary>
    [MaxLength(RuleVersionMaxLength)]
    public string? RuleVersion { get; set; }

    /// <summary>The signed amount in the statement currency: positive is owed to the owner.</summary>
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    /// <summary>The line this one reverses (a line is reversed at most once).</summary>
    public Guid? ReversesEntryId { get; set; }

    [MaxLength(TextMaxLength)]
    public string? Description { get; set; }

    public DateTime PostedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? PostedByUserId { get; set; }
}
