using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A confirmed payout (<see cref="OwnerPayout"/>) paid by a wire batch (E16/F01/US01/T01): the line of the file that pays it. A payout is in <b>one batch
/// only</b>, ever (the unique index <see cref="PayoutIndexName"/> refuses a second row for it, whatever two requests do at the same moment), and the amount
/// of its transfer is its net after the withholding, written here as the file says it.
/// </summary>
[Table("WireBatchPayouts")]
public class WireBatchPayout : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="OwnerPayoutId"/>: a payout is paid by one batch only.</summary>
    public const string PayoutIndexName = "UIX_WireBatchPayouts_OwnerPayoutId";

    /// <summary>The longest <see cref="EndToEndId"/>: the <c>Max35Text</c> of ISO 20022.</summary>
    public const int EndToEndIdMaxLength = 35;

    /// <summary>The first letters of every <see cref="EndToEndId"/>: the transfers of CasaZen are told from the others on the bank statement.</summary>
    public const string EndToEndIdPrefix = "CZ";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the batch; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The batch whose file pays the payout.</summary>
    public Guid WireBatchId { get; set; }

    /// <summary>The confirmed payout this transfer pays.</summary>
    public Guid OwnerPayoutId { get; set; }

    /// <summary>The owner who gets the transfer (the beneficiary of the payout).</summary>
    public Guid PropertyOwnerId { get; set; }

    /// <summary>What the transfer pays: the net amount of the payout (the gross minus the withholding), two decimals, in the currency of the batch.</summary>
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    /// <summary>
    /// The end-to-end id of the transfer in the file (<c>CZ</c> and the payout id without hyphens, 34 characters): unique in the file and the key the bank
    /// gives back on its statement, so a movement is told from the payout it pays. Not personal data.
    /// </summary>
    [Required]
    [MaxLength(EndToEndIdMaxLength)]
    public string EndToEndId { get; set; } = string.Empty;

    /// <summary>When the payout was added to the batch (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The end-to-end id of the transfer that pays <paramref name="payoutId"/>: <c>CZ</c> and the 32 digits of its id.</summary>
    public static string EndToEndIdOf(Guid payoutId) => EndToEndIdPrefix + payoutId.ToString("N");
}
