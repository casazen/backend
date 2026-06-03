using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("LeaseRegistrations")]
public class LeaseRegistration
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid LeaseContractId { get; set; }

    [ForeignKey(nameof(LeaseContractId))]
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    [Required]
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Pending;

    [MaxLength(200)]
    public string? ExternalRegistrationId { get; set; }

    [MaxLength(100)]
    public string? RegistrationCode { get; set; }

    [MaxLength(1000)]
    public string? ReceiptStoragePath { get; set; }

    public DateTime? SubmittedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}
