using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A debit started from a Financial Connections account (<c>fca_…</c>) as a platform PaymentIntent. The linked account
/// id is the only bank reference stored. A rent installment pointing at this row is paid only when Stripe confirms the
/// PaymentIntent, or when the landlord declares the existing offline payment.
/// </summary>
[Table("BankLinkPayments")]
public class BankLinkPayment : ITenantOwned
{
    public const int StripeIdMaxLength = 255;
    public const int PaymentMethodTypeMaxLength = 32;
    public const int CurrencyMaxLength = 3;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    /// <summary>Set when the payer is the tenant of that lease, or when the landlord pays one installment of it.</summary>
    public Guid? LeaseContractId { get; set; }

    /// <summary>The rent due this debit was started for. Null for a transfer that is not a rent installment.</summary>
    public Guid? RentLedgerEntryId { get; set; }

    public BankLinkPayer Payer { get; set; }

    [Required, MaxLength(StripeIdMaxLength)]
    public string FinancialConnectionsAccountId { get; set; } = string.Empty;

    [Required, MaxLength(StripeIdMaxLength)]
    public string StripePaymentIntentId { get; set; } = string.Empty;

    [MaxLength(StripeIdMaxLength)]
    public string? StripePaymentMethodId { get; set; }

    /// <summary>Stripe PaymentMethod type used for the debit (<c>us_bank_account</c>).</summary>
    [Required, MaxLength(PaymentMethodTypeMaxLength)]
    public string PaymentMethodType { get; set; } = string.Empty;

    public long AmountCents { get; set; }

    [Required, MaxLength(CurrencyMaxLength)]
    public string Currency { get; set; } = string.Empty;

    public BankLinkPaymentStatus Status { get; set; } = BankLinkPaymentStatus.Processing;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
