using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("LeaseContracts")]
public class LeaseContract
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid PropertyId { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public virtual Property Property { get; set; } = null!;

    [Required]
    public LeaseStatus Status { get; set; } = LeaseStatus.Draft;

    [Required]
    public FiscalRegime FiscalRegime { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    [Required]
    public DateTime EndDate { get; set; }

    [Required]
    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyRent { get; set; }

    public DateTime RegistrationDeadline { get; set; }

    [MaxLength(500)]
    public string? ExternalSigningSessionId { get; set; }

    [MaxLength(1000)]
    public string? SignedPdfStoragePath { get; set; }

    public bool ErasureRequested { get; set; } = false;

    public DateTime DataRetentionUntil { get; set; }

    public virtual ICollection<Party> Parties { get; set; } = [];
    public virtual LeaseRegistration? Registration { get; set; }
    public virtual ICollection<LeaseEvent> Events { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasExtraEUTenant => Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU);
}
