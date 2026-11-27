using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// What a person of the agency decided about the tax regime of one stay of a company tenant in the Italian property of a private owner (E18/F01/US03/T01, ⚖ I7,
/// NF-117): the two readings (the tax agency circular and the Court of Cassation) disagree, so CasaZen changes nothing by itself: it raises a «Da fare»
/// (<c>company_tenant_decision</c>) and records the decision that is taken. The withholding facts read the decision: a stay decided <see cref="ShortLetTaxRegime.NotShortLet"/>
/// is not withheld.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Append only.</b> A decision is never edited: a new one for the same booking replaces it as the current one (the latest, <see cref="Casazen.Core.Services.OwnerTaxRules.CurrentDecision"/>),
/// and the earlier ones stay as the record of who decided what and when. A payout already confirmed keeps its own evidence.</item>
/// <item><b>The booking</b> is a plain id with no foreign key: the decision is tax evidence and outlives the booking it was about.</item>
/// <item><b>The reason</b> is a note of the agency (why this regime, for example «confirmed by the accountant»): never shown to the guest, never in a log, never in the
/// «Da fare». It should hold no personal data of the guest.</item>
/// </list>
/// </remarks>
[Table("ShortLetTaxDecisions")]
public class ShortLetTaxDecision : ITenantOwned
{
    public const string BookingIndexName = "IX_ShortLetTaxDecisions_OrgId_BookingId";

    public const int ReasonMaxLength = 500;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public ShortLetTaxRegime Regime { get; set; }

    [Required, MaxLength(ReasonMaxLength)]
    public string Reason { get; set; } = string.Empty;

    public DateTime DecidedAt { get; set; }

    [Required, MaxLength(UserIdMaxLength)]
    public string DecidedByUserId { get; set; } = string.Empty;
}
