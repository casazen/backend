using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.TouristTax;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

public enum TouristTaxExemptionKind
{
    None = 0,

    Age = 1,

    Host = 2,
}

/// <summary>
/// The tourist tax of a stay as the host confirmed it, guest by guest (NF-46, ADR-037-R4): the record that stands for the
/// <c>TouristTaxCalculated</c> log, kept 10 years after the check-out for the comune's checks. One per booking; a new confirmation replaces it.
/// </summary>
/// <remarks>
/// <para>No name, birth date, age or document is stored here or in the lines: a line is a position of the stay, an exemption kind and code, and an
/// amount. <see cref="CalculationHash"/> is the SHA-256 of the dates, the rates used, the positions, the amounts and the collector, so a later
/// change of any of them shows the record as stale; it holds neither the age nor the reason of an exemption (an unsalted hash of a reason can be
/// tried against the short list of reasons, even after the reason was erased), which are compared with the lines instead. Tenant-owned; the lines
/// are reached only through it.</para>
/// <para>The row also keeps the host's declaration of who collected the tax of a channel stay with no channel rule for its comune
/// (<see cref="DeclaredCollector"/>): a declaration made before any confirmation creates the row unconfirmed (<see cref="ConfirmedAt"/>
/// null, no lines). Only a row with <see cref="ConfirmedAt"/> set is a confirmed calculation.</para>
/// </remarks>
[Table("StayTouristTaxes")]
public class StayTouristTax : ITenantOwned
{
    public const int RetentionYears = 10;

    public const int CalculationHashLength = 64;

    public static readonly string ErasedCalculationHash = new('0', CalculationHashLength);

    public const int UserIdMaxLength = 255;

    public const string BookingIndexName = "UIX_StayTouristTaxes_BookingId";

    public const int CurrencyMaxLength = 3;

    public const int StripeIdMaxLength = 255;

    public const string PaymentIntentIndexName = "UIX_StayTouristTaxes_StripePaymentIntentId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public TouristTaxQuoteStatus Status { get; set; }

    public int Nights { get; set; }

    public int TaxableNights { get; set; }

    [Precision(18, 2)]
    public decimal Total { get; set; }

    [Precision(18, 2)]
    public decimal AmountDue { get; set; }

    public TouristTaxCollector? Collector { get; set; }

    public Guid? ChannelRuleId { get; set; }

    [MaxLength(CalculationHashLength)]
    public string? CalculationHash { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public TouristTaxCollector? DeclaredCollector { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? DeclaredByUserId { get; set; }

    public DateTime? DeclaredAt { get; set; }

    public DateOnly RetainUntil { get; set; }

    // ─── The collection of the tax (E25/F01/US01/T01, NF-119, decision O12) ─────────────────────────────────────────────────────────────────
    // What the guest paid online is recorded here and never as a Payment row: the owner ledger, the balance of the booking and the revenue do not
    // change. A row created by the collection before any confirmation is unconfirmed (ConfirmedAt null, no lines), like the host's declaration.

    public TouristTaxCollectionStatus CollectionStatus { get; set; } = TouristTaxCollectionStatus.ToCollect;

    [Precision(18, 2)]
    public decimal CollectedAmount { get; set; }

    [MaxLength(CurrencyMaxLength)]
    public string? Currency { get; set; }

    public DateTime? CollectedAt { get; set; }

    [MaxLength(StripeIdMaxLength)]
    public string? StripePaymentIntentId { get; set; }

    [MaxLength(StripeIdMaxLength)]
    public string? StripeAccountId { get; set; }

    [Precision(18, 2)]
    public decimal RefundedAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<StayTouristTaxGuest> Guests { get; set; } = [];

    public static DateOnly RetentionFor(DateOnly checkOut) => checkOut.AddYears(RetentionYears);
}

[Table("StayTouristTaxGuests")]
public class StayTouristTaxGuest
{
    public const string PositionIndexName = "UIX_StayTouristTaxGuests_StayTouristTaxId_Position";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid StayTouristTaxId { get; set; }

    public int Position { get; set; }

    public TouristTaxExemptionKind ExemptionKind { get; set; }

    /// <summary>
    /// The reason code (<see cref="TouristTaxExemptionReason.Code"/>); null for a paying guest, and cleared when the guest's data are erased
    /// (the reason can reveal a health condition, art. 9 GDPR), while the kind and the amount stay for the fiscal retention. Encrypted at rest
    /// like the reason on the guest (<c>EncryptedColumns</c>, the purpose of the document): the column has no length, it holds the payload.
    /// </summary>
    public string? ExemptionCode { get; set; }

    public int TaxableNights { get; set; }

    [Precision(18, 2)]
    public decimal Amount { get; set; }
}
