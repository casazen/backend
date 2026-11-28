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

    public LeaseContractType ContractType { get; set; }

    public LeaseTaxRegime? TaxRegime { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? SecurityDeposit { get; set; }

    public LeaseConcordatoAssessment? ConcordatoAssessment { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    private DateTime _endDate;

    [Required]
    public DateTime EndDate
    {
        get => _endDate;
        set
        {
            if (CurrentTermEndDate == default || CurrentTermEndDate == _endDate)
                CurrentTermEndDate = value;

            _endDate = value;
        }
    }

    [Required]
    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyRent { get; set; }

    /// <summary>
    /// Date of the stipula: the Europe/Rome calendar day on which every party had signed (midnight UTC, FD-06). Null
    /// until the contract is signed by all parties, and for older leases whose signing was never recorded. Set only
    /// through <see cref="RecordStipula"/> (LT-04).
    /// </summary>
    public DateTime? StipulaDate { get; set; }

    public DateTime? RegistrationDeadline { get; set; }

    [MaxLength(500)]
    public string? ExternalSigningSessionId { get; set; }

    [MaxLength(1000)]
    public string? SignedPdfStoragePath { get; set; }

    [MaxLength(200)]
    public string? StipulaDeclaredByUserId { get; set; }

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

    [MaxLength(200)]
    public string? QuesturaCommunicationDeclaredByUserId { get; set; }

    /// <summary>
    /// A party asked for the erasure of their data (art. 17 GDPR, LT-12): the parties are anonymized as soon as the lease
    /// has ended (the day after <see cref="CurrentTermEndDate"/>), without waiting for the retention period. Never reset.
    /// </summary>
    public bool ErasureRequested { get; set; } = false;

    public DateTime? ErasureRequestedAt { get; set; }

    /// <summary>
    /// When every party of the lease was anonymized (LT-12, A7-18), by an erasure request or by the retention period
    /// <c>Gdpr:Retention:LeaseParties</c> counted from <see cref="CurrentTermEndDate"/>. Null while at least one party keeps its data.
    /// The retention end is computed on read from the configured period, never stored.
    /// </summary>
    public DateTime? PartiesAnonymizedAt { get; set; }

    /// <summary>
    /// The values the landlord typed in the wizard, as a JSON document (<c>LeaseDraftValues</c>), <b>encrypted at rest</b>
    /// (<c>EncryptedColumns</c>, purpose <c>Casazen.LeaseContract.Draft</c>): names, fiscal codes and residences of the parties
    /// included. It is the whole typed state while the data of the draft is not complete (some value is missing or not valid,
    /// so the columns hold starting values); <c>null</c> once it is complete, and for every lease that was not saved through the
    /// wizard (then the columns and the parties are the truth). Never read by anything but the draft service, never in a
    /// log or in a masked DTO.
    /// </summary>
    public string? DraftData { get; set; }

    public string? OpenapiRegistrationData { get; set; }

    [ConcurrencyCheck]
    public int OpenapiRegistrationVersion { get; set; }


    /// <summary>
    /// Optimistic version of the draft: every change of the guided draft (a step saved, the review confirmed or withdrawn, the
    /// contract marked as downloaded for signature) increases it, and a save made with an older number is refused with 409
    /// <c>lease_draft_conflict</c>, so two browser tabs never overwrite each other without notice. Not a column of the
    /// concurrency of the other writers of the lease (signature, registration).
    /// </summary>
    public int DraftVersion { get; set; }

    public int WizardMaxStep { get; set; }

    public DateTime? ReviewConfirmedAt { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the values of the first five steps at the moment the review was confirmed (<c>LeaseDraftFingerprint</c>):
    /// a later save changes the confirmation only if it changes a value, not when the autosave repeats the same ones. <c>null</c>
    /// without a confirmation. No personal data can be read back from it.
    /// </summary>
    [MaxLength(64)]
    public string? ReviewFingerprint { get; set; }

    public DateTime? SentForSignatureAt { get; set; }

    public LeaseOrigin Origin { get; set; } = LeaseOrigin.Created;

    /// <summary>
    /// Marks a draft created for the ConcordatoFacile flow (ADR-004): it complements <see cref="ContractType"/> (Concordato) and
    /// <see cref="Origin"/> (<see cref="LeaseOrigin.Imported"/>: the contract is the partner's, signed outside our models, CF-IMPORT).
    /// True only for minimal drafts created through the CF payment entry point; it stays true when the draft is imported as a signed
    /// contract. Protects the draft from deletion (by the owner and by the retention job) while a CF purchase is pending or completed.
    /// </summary>
    public bool IsConcordatoFacileSource { get; set; } = false;

    public DateTime CurrentTermEndDate { get; set; }

    /// <summary>
    /// The day the landlord or the tenant gave notice that the lease will not renew (the landlord records it, with <see cref="NoticeFrom"/>
    /// saying who gave it); <c>null</c> without a notice. While it is set the lease ends with its current period and does not renew.
    /// CasaZen does not judge whether the notice was valid in law (decision LR-D29).
    /// </summary>
    public DateTime? NoticeOn { get; set; }

    public LeaseNoticeParty? NoticeFrom { get; set; }

    public DateTime? TerminatedOn { get; set; }

    // ─── The need of a transitory contract (TR) ────────────────────────────────────────────────────────────────────────
    // A transitory contract (1 to 18 months) states the need of the landlord or of the tenant that justifies its term, and proves it with
    // documentation when it lasts more than 30 days (D.M. 16/01/2017 art. 2 c. 4). The model prints them in its art. 2, so they live in the
    // columns of the lease like the rest of what the contract prints; null for every other type and for a contract signed elsewhere.

    public PartyRole? TransitoryParty { get; set; }

    public string? TransitoryNeed { get; set; }

    public string? TransitoryDocumentation { get; set; }

    /// <summary>
    /// Stripe customer of the tenant's bank link for this lease only (<c>cus_…</c>). Not the landlord's billing customer.
    /// </summary>
    [MaxLength(255)]
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// Financial Connections account (<c>fca_…</c>) the tenant linked for this lease. A Stripe account id, never an IBAN.
    /// Null while the tenant pays by card or by the manual bank transfer.
    /// </summary>
    [MaxLength(255)]
    public string? FinancialConnectionsAccountId { get; set; }

    public virtual ICollection<Party> Parties { get; set; } = [];
    public virtual ICollection<LeaseSigner> Signers { get; set; } = [];
    public virtual LeaseRegistration? Registration { get; set; }
    public virtual ICollection<LeaseEvent> Events { get; set; } = [];
    public virtual RentSchedule? RentSchedule { get; set; }

    public virtual LeaseCondominiumTerms? CondominiumTerms { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasExtraEUTenant => Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU);

    public void SetContractTerms(LeaseContractType contractType, LeaseTaxRegime? taxRegime)
    {
        ContractType = contractType;
        TaxRegime = taxRegime;
        FiscalRegime = LeaseContractTerms.LegacyFiscalRegime(contractType, taxRegime);
    }

    public void RecordStipula(DateTime signedAt)
    {
        StipulaDate = RliRegistrationDeadline.ToStoredDate(signedAt);
        RegistrationDeadline = RliRegistrationDeadline.Compute(StipulaDate.Value, StartDate);
    }
}
