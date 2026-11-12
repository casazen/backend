using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public enum TenantPaymentDeclarationStatus
{
    Submitted,
    Verified,
    Rejected,
    Superseded,
}

/// <summary>
/// A tenant's statement that a rent transfer was made. It never marks the installment paid: only the landlord,
/// a bank reconciliation or the existing online-payment flow may do that.
/// </summary>
[Table("TenantPaymentDeclarations")]
public sealed class TenantPaymentDeclaration : ITenantOwned
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    public Guid LeaseContractId { get; set; }
    public Guid RentLedgerEntryId { get; set; }
    [Required, MaxLength(200)] public string SubmittedByUserId { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    public DateOnly TransferDate { get; set; }
    [MaxLength(200)] public string? BankReference { get; set; }
    [MaxLength(500)] public string? Note { get; set; }
    [MaxLength(1000)] public string? ReceiptStorageKey { get; set; }
    public TenantPaymentDeclarationStatus Status { get; set; } = TenantPaymentDeclarationStatus.Submitted;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    public LeaseContract LeaseContract { get; set; } = null!;
    public RentLedgerEntry RentLedgerEntry { get; set; } = null!;
}
