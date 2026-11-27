using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The commission a channel keeps on the stays of an agency, when the booking does not say it (E13/F01/US01/T01, ⚖ R2): <see cref="Percent"/> of
/// <see cref="Base"/> for the stays of <see cref="Channel"/> that check out from <see cref="ValidFrom"/> to <see cref="ValidTo"/> (both included). The owner
/// ledger uses it only when the reservation brings no commission (<see cref="Booking.OtaCommission"/>); without a rule in force the configured default
/// of the channel applies (<c>OwnerAccounting:ChannelCommissionDefaults</c>). Saved as a whole list (<c>PUT api/owner-accounting/channel-commission-rules</c>).
/// </summary>
[Table("OrgChannelCommissionRules")]
public class OrgChannelCommissionRule : ITenantOwned
{
    /// <summary>Unique (<see cref="OrgId"/>, <see cref="Channel"/>, <see cref="ValidFrom"/>).</summary>
    public const string ChannelValidFromIndexName = "UIX_OrgChannelCommissionRules_OrgId_Channel_ValidFrom";

    /// <summary>The longest <see cref="Channel"/>: the name of a <see cref="BookingSource"/>.</summary>
    public const int ChannelMaxLength = 32;

    /// <summary>The longest <see cref="CreatedByUserId"/>.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the agency; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The channel, as the name of its <see cref="BookingSource"/> (<c>BookingCom</c>, <c>Airbnb</c>...): only the portals have a rule.</summary>
    [Required]
    [MaxLength(ChannelMaxLength)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>The commission in percent, 0 to 100 with two decimals.</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal Percent { get; set; }

    /// <summary>What <see cref="Percent"/> is a percentage of: the gross of the stay without the tourist tax by default.</summary>
    public CommissionBase Base { get; set; } = CommissionBase.GrossExclTouristTax;

    /// <summary>The first check-out day the rule applies to (Europe/Rome).</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>The last check-out day the rule applies to (included); <c>null</c> = no end.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>When the rule was saved (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Who saved it.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
