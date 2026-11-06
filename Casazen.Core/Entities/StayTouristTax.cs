using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
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
/// amount. <see cref="CalculationHash"/> is the SHA-256 of the rates used and of the guest inputs, so a later change of either shows the record
/// as stale. Tenant-owned; the lines are reached only through it.</para>
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

    /// <summary>Length of <see cref="ConfirmedByUserId"/>: the one of <c>User.Id</c>.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Name of the unique index on <see cref="BookingId"/>.</summary>
    public const string BookingIndexName = "UIX_StayTouristTaxes_BookingId";

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
    /// (the reason can reveal a health condition), while the kind and the amount stay for the fiscal retention.
    /// </summary>
    [MaxLength(TouristTaxExemptionReason.CodeMaxLength)]
    public string? ExemptionCode { get; set; }

    /// <summary>Nights on which this guest was taxed.</summary>
    public int TaxableNights { get; set; }

    /// <summary>Tax of the guest in euros.</summary>
    [Precision(18, 2)]
    public decimal Amount { get; set; }
}
