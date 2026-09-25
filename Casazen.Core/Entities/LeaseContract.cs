using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
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

    /// <summary>
    /// Legacy combined value, derived from <see cref="ContractType"/> and <see cref="TaxRegime"/> at creation
    /// (<see cref="LeaseContractTerms.LegacyFiscalRegime"/>). Still the key of the contract template, the IMU notice
    /// and the cedolare advisory.
    /// </summary>
    [Required]
    public FiscalRegime FiscalRegime { get; set; }

    /// <summary>Contract type (LT-10, A7-13): fixes the term rules and the canone concordato range.</summary>
    public LeaseContractType ContractType { get; set; }

    /// <summary>
    /// Tax regime chosen by the landlord (LT-10). Null only for canone concordato leases created before the contract type
    /// was separated from the tax regime: the old value did not say it.
    /// </summary>
    public LeaseTaxRegime? TaxRegime { get; set; }

    /// <summary>Security deposit in euros (template data, LT-03); null when not declared.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? SecurityDeposit { get; set; }

    /// <summary>Characteristics and range of a canone concordato lease (LT-10, A7-12); null for the other types.</summary>
    public LeaseConcordatoAssessment? ConcordatoAssessment { get; set; }

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

    /// <summary>
    /// Key of the contract signed by every party in the private bucket (FD-07, LT-02): uploaded by the landlord (offline
    /// signature) or copied from the e-signature provider. Served only by the authenticated lease endpoint.
    /// </summary>
    [MaxLength(1000)]
    public string? SignedPdfStoragePath { get; set; }

    /// <summary>
    /// User who declared the stipula date (offline signature or later declaration, LT-02); null when the provider
    /// recorded the signature or no declaration was made.
    /// </summary>
    [MaxLength(200)]
    public string? StipulaDeclaredByUserId { get; set; }

    /// <summary>
    /// Day the property was (or will be) delivered to the tenant, declared by the landlord (LT-07): midnight UTC of the
    /// Europe/Rome date. Null when not declared: the start date is used (<see cref="QuesturaCommunicationDeadline"/>).
    /// </summary>
    public DateTime? PropertyDeliveryDate { get; set; }

    /// <summary>
    /// Date on which the landlord declares they sent the communication to the public-security authority for an extra-EU
    /// tenant (art. 7 D.Lgs. 286/1998, LT-07): midnight UTC of the Rome date. Null until that explicit declaration; a
    /// CasaZen reminder never sets it.
    /// </summary>
    public DateTime? QuesturaCommunicationDate { get; set; }

    /// <summary>Key of the optional receipt of the Questura communication in the private bucket (FD-07, LT-07).</summary>
    [MaxLength(1000)]
    public string? QuesturaCommunicationReceiptPath { get; set; }

    /// <summary>User who declared the Questura communication (LT-07).</summary>
    [MaxLength(200)]
    public string? QuesturaCommunicationDeclaredByUserId { get; set; }

    public bool ErasureRequested { get; set; } = false;

    public DateTime DataRetentionUntil { get; set; }

    public virtual ICollection<Party> Parties { get; set; } = [];
    public virtual ICollection<LeaseSigner> Signers { get; set; } = [];
    public virtual LeaseRegistration? Registration { get; set; }
    public virtual ICollection<LeaseEvent> Events { get; set; } = [];
    public virtual RentSchedule? RentSchedule { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasExtraEUTenant => Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU);

    /// <summary>
    /// Sets the contract type and the tax regime, and the legacy <see cref="FiscalRegime"/> derived from them (LT-10).
    /// </summary>
    public void SetContractTerms(LeaseContractType contractType, LeaseTaxRegime? taxRegime)
    {
        ContractType = contractType;
        TaxRegime = taxRegime;
        FiscalRegime = LeaseContractTerms.LegacyFiscalRegime(contractType, taxRegime);
    }

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
