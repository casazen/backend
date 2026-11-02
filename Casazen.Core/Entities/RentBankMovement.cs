using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A credit of a bank statement that the landlord confirmed as the payment of a rent installment (import of the statement of the Canoni
/// page). It keeps what is needed to import the same movement only once and to recognize the same payer next time, and nothing that
/// reads as a person: not the file, not the reason of the payment, not the payer's name or IBAN, only hashes of them.
/// </summary>
/// <remarks>
/// <para><see cref="Fingerprint"/> is a SHA-256 of the movement and the org (<c>BankMovementFingerprint</c>): unique per org, so the same
/// movement in two overlapping files, or the same file twice, is imported once. <see cref="PayerIbanHash"/> and <see cref="PayerNameHash"/>
/// (<c>BankPayerHash</c>: HMAC-SHA256 with a key that is not in the database) let the matcher recognize who paid this lease before; they are
/// cleared when the tenants of the lease are anonymized, and never written for a lease whose tenants asked to be erased.</para>
/// <para>The installment is paid through the same offline path as a payment the landlord declares by hand
/// (<c>IRentBillingService.MarkPaidOfflineAsync</c>): this row says only which statement line it came from. It is saved <em>before</em> the
/// installment is paid, as the claim on the credit that the unique index of the fingerprint turns into one winner when two requests race,
/// and taken back if the payment is refused.</para>
/// </remarks>
[Table("RentBankMovements")]
public class RentBankMovement : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public Guid RentLedgerEntryId { get; set; }

    /// <summary>SHA-256 (hex, lower case) of the movement as the bank wrote it, the org and the order among identical lines.</summary>
    [Required]
    [MaxLength(64)]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Keyed hash of the payer's IBAN (with the org); null when the file had none or there is no key. Cleared when the tenants are anonymized.</summary>
    [MaxLength(64)]
    public string? PayerIbanHash { get; set; }

    /// <summary>Keyed hash of the payer's name (with the org); null when the file had none or there is no key. Cleared when the tenants are anonymized.</summary>
    [MaxLength(64)]
    public string? PayerNameHash { get; set; }

    /// <summary>Value date of the credit: the day the installment was declared paid.</summary>
    public DateOnly ValueDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>The user who confirmed the match.</summary>
    [Required]
    [MaxLength(200)]
    public string ConfirmedByUserId { get; set; } = string.Empty;

    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;

    public LeaseContract LeaseContract { get; set; } = null!;
    public RentLedgerEntry RentLedgerEntry { get; set; } = null!;
    public Org Org { get; set; } = null!;
}
