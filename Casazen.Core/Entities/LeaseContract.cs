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

    private DateTime _endDate;

    /// <summary>
    /// Last day (included) of the <b>first period</b> of the contract, as the contract states it: what the contract template, the RLI
    /// registration and the first rent plan are written from. It does not move when the lease renews by itself or is ended early: that
    /// is <see cref="CurrentTermEndDate"/>, and every reader that asks when the lease ends reads that one (LR-14).
    /// </summary>
    [Required]
    public DateTime EndDate
    {
        get => _endDate;
        set
        {
            // While the lease is in its first period the current period ends where the contract ends, so a writer that knows only the
            // contract (the guided draft, the complete creation) leaves the lease consistent. Once the lease has renewed or been ended
            // early the two ends differ and this setter leaves the current one alone. EF fills the field, not this setter.
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

    /// <summary>
    /// A party asked for the erasure of their data (art. 17 GDPR, LT-12): the parties are anonymized as soon as the lease
    /// has ended (the day after <see cref="CurrentTermEndDate"/>), without waiting for the retention period. Never reset.
    /// </summary>
    public bool ErasureRequested { get; set; } = false;

    /// <summary>When the erasure was first requested (UTC instant); null without a request.</summary>
    public DateTime? ErasureRequestedAt { get; set; }

    /// <summary>
    /// When every party of the lease was anonymized (LT-12, A7-18), by an erasure request or by the retention period
    /// <c>Gdpr:Retention:LeaseParties</c> counted from <see cref="CurrentTermEndDate"/>. Null while at least one party keeps its data.
    /// The retention end is computed on read from the configured period, never stored.
    /// </summary>
    public DateTime? PartiesAnonymizedAt { get; set; }

    // ─── The guided draft (LR-12) ────────────────────────────────────────────────────────────────────────────────────
    // A draft is saved step by step with values that may be incomplete or wrong, but the columns above stay NOT NULL and every
    // reader of the lease (views, occupancy, privacy, RLI) keeps reading them. So a draft starts with the starting values of
    // LeaseDraftDefaults, a value is written into its column only when it is valid, and what the columns cannot hold lives in
    // DraftData until the data of the draft is complete.

    /// <summary>
    /// The values the landlord typed in the wizard, as a JSON document (<c>LeaseDraftValues</c>), <b>encrypted at rest</b>
    /// (<c>EncryptedColumns</c>, purpose <c>Casazen.LeaseContract.Draft</c>): names, fiscal codes and residences of the parties
    /// included. It is the whole typed state while the data of the draft is not complete (some value is missing or not valid,
    /// so the columns hold starting values); <c>null</c> once it is complete, and for every lease that was not saved through the
    /// wizard (then the columns and the parties are the truth). Never read by anything but the draft service, never in a
    /// log or in a masked DTO.
    /// </summary>
    public string? DraftData { get; set; }

    /// <summary>
    /// Optimistic version of the draft: every change of the guided draft (a step saved, the review confirmed or withdrawn, the
    /// contract marked as downloaded for signature) increases it, and a save made with an older number is refused with 409
    /// <c>lease_draft_conflict</c>, so two browser tabs never overwrite each other without notice. Not a column of the
    /// concurrency of the other writers of the lease (signature, registration).
    /// </summary>
    public int DraftVersion { get; set; }

    /// <summary>
    /// The furthest step (0 to 8) the landlord reached in the wizard. A data step is "done" only if nothing is missing and the
    /// landlord has been there (<c>LeaseDraftProgress</c>). 1 for a draft created with only the property; 5 for a lease created
    /// complete in one call (<c>POST api/leases</c>) and for the drafts that existed before LR-12.
    /// </summary>
    public int WizardMaxStep { get; set; }

    /// <summary>
    /// When the landlord confirmed the review (the five data steps checked, nothing blocking). <c>null</c> until then and again
    /// as soon as a value of the first five steps changes or the confirmation is withdrawn.
    /// </summary>
    public DateTime? ReviewConfirmedAt { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the values of the first five steps at the moment the review was confirmed (<c>LeaseDraftFingerprint</c>):
    /// a later save changes the confirmation only if it changes a value, not when the autosave repeats the same ones. <c>null</c>
    /// without a confirmation. No personal data can be read back from it.
    /// </summary>
    [MaxLength(64)]
    public string? ReviewFingerprint { get; set; }

    /// <summary>
    /// When the landlord downloaded the contract to have it signed on paper (the "scaricato per la firma" of the wizard). It does
    /// not change <see cref="Status"/>: an offline lease is a draft until the signed copy is uploaded. <c>null</c> until then, and
    /// again when it is withdrawn or the review is reopened.
    /// </summary>
    public DateTime? SentForSignatureAt { get; set; }

    /// <summary>How the lease came to be in CasaZen (<see cref="LeaseOrigin"/>); fixed at creation.</summary>
    public LeaseOrigin Origin { get; set; } = LeaseOrigin.Created;

    // ─── The end of the lease that is in force (LR-14) ────────────────────────────────────────────────────────────────
    // A libero (4+4) or concordato (3+2) lease that nobody gave notice of renews by itself, so the end the contract states is only the
    // end of its first period. The end that tells whether the lease is over, ties a property up or may be anonymized is this one. All the
    // dates are the Europe/Rome calendar day as midnight UTC.

    /// <summary>
    /// Last day (included) of the period the lease is in now. Equal to <see cref="EndDate"/> until the lease renews by itself
    /// (<see cref="LeaseRenewal.NextEnd"/>, the hourly job <c>lease-term-renewal</c>) or is ended early (<see cref="TerminatedOn"/>).
    /// A lease that had already ended when the column was added keeps its end and is recorded as concluded (no retroactive renewal).
    /// </summary>
    public DateTime CurrentTermEndDate { get; set; }

    /// <summary>
    /// The day the landlord or the tenant gave notice that the lease will not renew (the landlord records it, with <see cref="NoticeFrom"/>
    /// saying who gave it); <c>null</c> without a notice. While it is set the lease ends with its current period and does not renew.
    /// CasaZen does not judge whether the notice was valid in law (decision LR-D29).
    /// </summary>
    public DateTime? NoticeOn { get; set; }

    /// <summary>Who gave the notice of <see cref="NoticeOn"/>; <c>null</c> without a notice.</summary>
    public LeaseNoticeParty? NoticeFrom { get; set; }

    /// <summary>
    /// The last day of a lease that was ended before the end of its period (early termination, LR-14), which is then
    /// <see cref="CurrentTermEndDate"/> too; <c>null</c> for a lease that was not ended early. Irreversible, and it stops the renewal. It
    /// is also what records as concluded a lease that was already over when CasaZen came to know it (no retroactive renewal): one that had
    /// ended before the renewal existed, or a contract signed or imported after its last day (<see cref="LeaseRenewal.ConcludeIfEnded"/>).
    /// </summary>
    public DateTime? TerminatedOn { get; set; }

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
