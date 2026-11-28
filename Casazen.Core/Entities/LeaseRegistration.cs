using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
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
    [JsonIgnore]
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    [Required]
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Pending;

    public RegistrationChannel Channel { get; set; } = RegistrationChannel.Provider;

    [MaxLength(200)]
    public string? ExternalRegistrationId { get; set; }

    [MaxLength(20)]
    public string? ProviderCode { get; set; }

    /// <summary>Registration number or protocol of the Agenzia delle Entrate, as written on the receipt.</summary>
    [MaxLength(100)]
    public string? RegistrationCode { get; set; }

    /// <summary>Date of the registration at the Agenzia delle Entrate (calendar date, UTC midnight).</summary>
    public DateTime? RegistrationDate { get; set; }

    [MaxLength(1000)]
    public string? ReceiptStoragePath { get; set; }

    /// <summary>Stable code of the last failure (<c>RliRegistrationFailureCodes</c>); null unless Failed.</summary>
    [MaxLength(100)]
    public string? FailureCode { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(200)]
    public string? DeclaredByUserId { get; set; }
}
