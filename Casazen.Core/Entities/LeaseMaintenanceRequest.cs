using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A repair the tenant of a lease asks the landlord (or the agency of the same org) to approve and pay.
/// Born <see cref="LeaseMaintenanceStatus.InAttesa"/>. Approval does not mark it paid: only a succeeded euro
/// debit does. Photos stay in the private bucket; <see cref="PhotosJson"/> holds their keys.
/// </summary>
[Table("LeaseMaintenanceRequests")]
public class LeaseMaintenanceRequest : ITenantOwned
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 4000;
    public const int ReasonMaxLength = 500;
    public const int UserIdMaxLength = 200;
    public const int CurrencyMaxLength = 3;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public LeaseContract LeaseContract { get; set; } = null!;

    [Required, MaxLength(TitleMaxLength)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(DescriptionMaxLength)]
    public string Description { get; set; } = string.Empty;

    public LeaseMaintenanceUrgency Urgency { get; set; }

    public LeaseMaintenanceStatus Status { get; set; } = LeaseMaintenanceStatus.InAttesa;

    [MaxLength(ReasonMaxLength)]
    public string? RejectionReason { get; set; }

    /// <summary>Euros of the debit that was started. Null until the landlord or the agency pays. Not a paid flag.</summary>
    public long? AmountCents { get; set; }

    [Required, MaxLength(CurrencyMaxLength)]
    public string Currency { get; set; } = "eur";

    /// <summary>The latest bank-link debit for this request. A succeeded one is what makes <see cref="Status"/> paid.</summary>
    public Guid? BankLinkPaymentId { get; set; }

    /// <summary>JSON array of stored photos. Empty when the tenant attached none.</summary>
    public string PhotosJson { get; set; } = "[]";

    [Required, MaxLength(UserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    [MaxLength(UserIdMaxLength)]
    public string? DecidedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DecidedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
