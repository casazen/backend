using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Entities;

[Table("LeaseContracts")]
public class LeaseContract : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid PropertyId { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant key (AC2). Inherited from the lease's property; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

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

    /// <summary>
    /// Date of the stipula: the Europe/Rome calendar day on which every party had signed (midnight UTC, FD-06). Null
    /// until the contract is signed by all parties, and for older leases whose signing was never recorded. Set only
    /// through <see cref="RecordStipula"/> (LT-04).
    /// </summary>
    public DateTime? StipulaDate { get; set; }

    /// <summary>
    /// RLI registration deadline fixed by the stipula: <c>min(StipulaDate, StartDate) + 30</c> days
    /// (<see cref="RliRegistrationDeadline"/>), midnight UTC of the Rome date. Null while the stipula is unknown; the
    /// deadline shown for a lease not signed yet is resolved on read by <see cref="RliRegistrationDeadline.Resolve(LeaseContract, DateTime)"/>.
    /// </summary>
    public DateTime? RegistrationDeadline { get; set; }

    [MaxLength(500)]
    public string? ExternalSigningSessionId { get; set; }

    [MaxLength(1000)]
    public string? SignedPdfStoragePath { get; set; }

    public bool ErasureRequested { get; set; } = false;

    public DateTime DataRetentionUntil { get; set; }

    public virtual ICollection<Party> Parties { get; set; } = [];
    public virtual LeaseRegistration? Registration { get; set; }
    public virtual ICollection<LeaseEvent> Events { get; set; } = [];
    public virtual RentSchedule? RentSchedule { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasExtraEUTenant => Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU);

    /// <summary>
    /// Records the stipula at full signature (every party signed through the e-sign provider, or a declared offline
    /// signature): <paramref name="signedAt"/> is the signing instant or the declared signing date, stored as its
    /// Europe/Rome calendar date, and the registration deadline is fixed from it (LT-04, A7-04).
    /// </summary>
    public void RecordStipula(DateTime signedAt)
    {
        StipulaDate = RliRegistrationDeadline.ToStoredDate(signedAt);
        RegistrationDeadline = RliRegistrationDeadline.Compute(StipulaDate.Value, StartDate);
    }
}
