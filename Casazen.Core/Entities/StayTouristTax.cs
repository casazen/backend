using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.TouristTax;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>Why a guest of a stay pays no tourist tax (<see cref="StayTouristTaxGuest.ExemptionKind"/>). Stored as an integer, append only.</summary>
public enum TouristTaxExemptionKind
{
    /// <summary>The guest pays.</summary>
    None = 0,

    /// <summary>Younger than the minimum age of the rate: applied by the calculator, never chosen.</summary>
    Age = 1,

    /// <summary>An exemption the host chose for the guest (a reason of <see cref="TouristTaxExemptionReason"/>).</summary>
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
    /// <summary>Years the record is kept after the check-out date (ADR-037-R4).</summary>
    public const int RetentionYears = 10;

    /// <summary>Length of <see cref="CalculationHash"/> (hex SHA-256).</summary>
    public const int CalculationHashLength = 64;

    /// <summary>
    /// The hash of a record whose exemption reasons were erased with their guests: all zeros, a value no calculation gives. The column keeps its
    /// value (a confirmed record always has a hash), and the record reads as stale from then on.
    /// </summary>
    public static readonly string ErasedCalculationHash = new('0', CalculationHashLength);

    /// <summary>Length of <see cref="ConfirmedByUserId"/>: the one of <c>User.Id</c>.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Name of the unique index on <see cref="BookingId"/>.</summary>
    public const string BookingIndexName = "UIX_StayTouristTaxes_BookingId";

    /// <summary>Length of <see cref="Currency"/>: an ISO 4217 code.</summary>
    public const int CurrencyMaxLength = 3;

    /// <summary>Length of <see cref="StripePaymentIntentId"/> and <see cref="StripeAccountId"/>.</summary>
    public const int StripeIdMaxLength = 255;

    /// <summary>Name of the unique index on <see cref="StripePaymentIntentId"/>, filtered to the rows that have one: an intent is the payment of one stay.</summary>
    public const string PaymentIntentIndexName = "UIX_StayTouristTaxes_StripePaymentIntentId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key, the org of the booking.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The stay (unique: one record per booking).</summary>
    public Guid BookingId { get; set; }

    /// <summary>Outcome of the calculation that was confirmed.</summary>
    public TouristTaxQuoteStatus Status { get; set; }

    /// <summary>Nights of the stay.</summary>
    public int Nights { get; set; }

    /// <summary>Nights subject to the tax (the first ones, up to the cap of the rate).</summary>
    public int TaxableNights { get; set; }

    /// <summary>Tax of the whole stay in euros (the sum of the lines).</summary>
    [Precision(18, 2)]
    public decimal Total { get; set; }

    /// <summary>What the host must collect: the total, or 0 when the channel collects it (ADR-037-R3).</summary>
    [Precision(18, 2)]
    public decimal AmountDue { get; set; }

    /// <summary>Who collects the tax; null until known (a channel booking with no rule and no declaration of the host).</summary>
    public TouristTaxCollector? Collector { get; set; }

    /// <summary>The channel rule that decided <see cref="Collector"/>, when one did; set to null if the rule is removed.</summary>
    public Guid? ChannelRuleId { get; set; }

    /// <summary>SHA-256 (hex) of the rate ids and of the guest inputs of the confirmed calculation; null until confirmed.</summary>
    [MaxLength(CalculationHashLength)]
    public string? CalculationHash { get; set; }

    /// <summary>When the host confirmed the calculation; null for a row that holds only the host's declaration of the collector.</summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>The user who confirmed it.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    /// <summary>
    /// Who collected the tax, as the host declared it for a channel stay with no channel rule for its comune; null when not declared. A channel
    /// rule, when one exists, always wins over it.
    /// </summary>
    public TouristTaxCollector? DeclaredCollector { get; set; }

    /// <summary>The user who made the declaration.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? DeclaredByUserId { get; set; }

    /// <summary>When the declaration was made (UTC).</summary>
    public DateTime? DeclaredAt { get; set; }

    /// <summary>The record is kept until this day: the check-out date plus <see cref="RetentionYears"/> years (ADR-037-R4).</summary>
    public DateOnly RetainUntil { get; set; }

    // ─── The collection of the tax (E25/F01/US01/T01, NF-119, decision O12) ─────────────────────────────────────────────────────────────────
    // What the guest paid online is recorded here and never as a Payment row: the owner ledger, the balance of the booking and the revenue do not
    // change. A row created by the collection before any confirmation is unconfirmed (ConfirmedAt null, no lines), like the host's declaration.

    /// <summary>
    /// The last state a write of the collection saved (<see cref="TouristTaxCollectionStatus"/>); <c>ToCollect</c> for every row written before it. Readers
    /// show the effective one of <c>TouristTaxCollectionRules.Evaluate</c>, which also reads the stay as it is now.
    /// </summary>
    public TouristTaxCollectionStatus CollectionStatus { get; set; } = TouristTaxCollectionStatus.ToCollect;

    /// <summary>What the guest paid online, as the payment provider received it (the amount of the PaymentIntent, never a figure of the client); 0 when nothing.</summary>
    [Precision(18, 2)]
    public decimal CollectedAmount { get; set; }

    /// <summary>The currency of the collection (upper case): the one of the PaymentIntent once paid, else the currency of the property.</summary>
    [MaxLength(CurrencyMaxLength)]
    public string? Currency { get; set; }

    /// <summary>When the online payment succeeded, or when the host recorded the payment at the property (UTC); null while nothing was collected.</summary>
    public DateTime? CollectedAt { get; set; }

    /// <summary>
    /// The PaymentIntent of the guest on the organization's connected account (<c>pi_…</c>): the open one while it is not paid, then the paid one. One per
    /// stay (<see cref="PaymentIntentIndexName"/>); a new amount cancels the open intent before another is created.
    /// </summary>
    [MaxLength(StripeIdMaxLength)]
    public string? StripePaymentIntentId { get; set; }

    /// <summary>The connected account (<c>acct_…</c>) the intent was created on: its refund and its events belong to it, whatever account the org has later.</summary>
    [MaxLength(StripeIdMaxLength)]
    public string? StripeAccountId { get; set; }

    /// <summary>What was refunded of <see cref="CollectedAmount"/>, as the charge reports it; never more than it.</summary>
    [Precision(18, 2)]
    public decimal RefundedAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>One line per guest of the stay.</summary>
    public List<StayTouristTaxGuest> Guests { get; set; } = [];

    /// <summary>The retention date of a stay that ends on <paramref name="checkOut"/>.</summary>
    public static DateOnly RetentionFor(DateOnly checkOut) => checkOut.AddYears(RetentionYears);
}

/// <summary>
/// The tax of one guest of a <see cref="StayTouristTax"/>: a position of the stay, never a person. Reached only through its record; a guest has
/// one line per booking (unique position), so the same guest is never taxed twice (AC-NF46-8).
/// </summary>
[Table("StayTouristTaxGuests")]
public class StayTouristTaxGuest
{
    /// <summary>Name of the unique index on (<see cref="StayTouristTaxId"/>, <see cref="Position"/>).</summary>
    public const string PositionIndexName = "UIX_StayTouristTaxGuests_StayTouristTaxId_Position";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid StayTouristTaxId { get; set; }

    /// <summary>The position of the guest in the stay (the <c>StayGuest.Position</c>).</summary>
    public int Position { get; set; }

    /// <summary>Why the guest pays nothing, if so.</summary>
    public TouristTaxExemptionKind ExemptionKind { get; set; }

    /// <summary>
    /// The reason code (<see cref="TouristTaxExemptionReason.Code"/>); null for a paying guest, and cleared when the guest's data are erased
    /// (the reason can reveal a health condition, art. 9 GDPR), while the kind and the amount stay for the fiscal retention. Encrypted at rest
    /// like the reason on the guest (<c>EncryptedColumns</c>, the purpose of the document): the column has no length, it holds the payload.
    /// </summary>
    public string? ExemptionCode { get; set; }

    /// <summary>Nights on which this guest was taxed.</summary>
    public int TaxableNights { get; set; }

    /// <summary>Tax of the guest in euros.</summary>
    [Precision(18, 2)]
    public decimal Amount { get; set; }
}
