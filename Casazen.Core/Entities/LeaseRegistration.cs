using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// RLI registration of a lease, one per lease (LT-01). <see cref="RegistrationStatus.Registered"/> only with the
/// official receipt stored in the private bucket (<see cref="ReceiptStoragePath"/>, database check constraint):
/// declared by the landlord (<see cref="RegistrationChannel.Manual"/>) or returned by a provider.
/// </summary>
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

    /// <summary>Registration number or protocol of the Agenzia delle Entrate, as written on the receipt.</summary>
    [MaxLength(100)]
    public string? RegistrationCode { get; set; }

    /// <summary>Date of the registration at the Agenzia delle Entrate (calendar date, UTC midnight).</summary>
    public DateTime? RegistrationDate { get; set; }

    /// <summary>Key of the receipt PDF in the private bucket; never returned to clients.</summary>
    [MaxLength(1000)]
    public string? ReceiptStoragePath { get; set; }

    /// <summary>Stable code of the last failure (<c>RliRegistrationFailureCodes</c>); null unless Failed.</summary>
    [MaxLength(100)]
    public string? FailureCode { get; set; }

    /// <summary>When the provider submission started (stale reservations are recovered from here).</summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>When the provider accepted the request.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>When CasaZen recorded the registration as done (manual declaration or provider receipt).</summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>User who declared a manual registration; never returned to clients.</summary>
    [MaxLength(200)]
    public string? DeclaredByUserId { get; set; }
}
