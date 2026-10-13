using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Safety checklist of a short-stay property under D.L. 145/2023 art. 13-ter, c. 7 (CO-07, A5-21), one per property.
/// It records what the host declares: CasaZen neither checks the unit nor certifies it. The facts of the unit
/// (<see cref="Entrepreneurial"/>, gas and combustion, floors) decide which items apply; the rules are in
/// <c>Casazen.Core.Regulatory.SafetyChecklistRules</c>. Source: <c>.claude/context/regulations/sicurezza.md</c>,
/// «Proposta di checklist per CO-07».
/// </summary>
[Table("PropertySafetyChecklists")]
public class PropertySafetyChecklist : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property (TN-2).</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>
    /// Version of the rules the answers were given under: 1 = imported from the old checklist (smoke detector,
    /// extinguisher, gas certificate), to review; 2 = current rules (<c>SafetyChecklistRules.SchemaVersion</c>).
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>Legal basis of <see cref="SchemaVersion"/> (<c>SafetyChecklistRules.LegalBasis</c>).</summary>
    [MaxLength(100)]
    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>SC-01: the host runs the rentals as a business (VAT number, company or presumption by number of flats).</summary>
    public bool? Entrepreneurial { get; set; }

    /// <summary>SC-03, first condition: the unit has a gas system or supply (mains, LPG or cylinders).</summary>
    public bool? HasGasSupply { get; set; }

    /// <summary>
    /// SC-03, second condition: combustion appliances in the unit. <c>null</c> = not answered, empty = none.
    /// </summary>
    public List<CombustionAppliance>? CombustionAppliances { get; set; }

    /// <summary>SC-02: number of floors of the unit, as declared by the host (what counts as a floor: sicurezza.md doubt 4).</summary>
    public int? FloorCount { get; set; }

    /// <summary>SC-02: floor area in m² of each floor of the unit, in floor order; <c>null</c> when not given.</summary>
    public List<decimal>? FloorAreasSqm { get; set; }

    /// <summary>SC-08: UTC instant of the host's final confirmation of the current answers; cleared by any change.</summary>
    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(255)]
    public string? ConfirmedBy { get; set; }

    /// <summary>Version of the declaration text the host confirmed (<c>SafetyChecklistRules.DeclarationTextVersion</c>).</summary>
    [MaxLength(50)]
    public string? ConfirmedTextVersion { get; set; }

    /// <summary>
    /// The old checklist of the property, verbatim (<c>Properties.SafetyChecklistJson</c> before CO-07): kept so the
    /// migration loses nothing. Never shown to guests.
    /// </summary>
    public string? LegacyChecklistJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string? UpdatedBy { get; set; }

    public virtual ICollection<PropertySafetyChecklistItem> Items { get; set; } = new List<PropertySafetyChecklistItem>();
}

/// <summary>
/// Answer of the host for one item of <see cref="PropertySafetyChecklist"/>. "Not applicable" is never stored: it
/// follows from the facts of the checklist and is computed on read.
/// </summary>
[Table("PropertySafetyChecklistItems")]
public class PropertySafetyChecklistItem : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the checklist (TN-2).</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Checklist))]
    public Guid ChecklistId { get; set; }
    public virtual PropertySafetyChecklist Checklist { get; set; } = null!;

    public SafetyItemCode Code { get; set; }

    /// <summary><c>null</c> = not answered.</summary>
    public SafetyItemAnswer? Answer { get; set; }

    /// <summary>Number of extinguishers (required when present) or of detectors (optional).</summary>
    public int? Quantity { get; set; }

    /// <summary>Where the devices are (entrance, kitchen, ...).</summary>
    [MaxLength(200)]
    public string? Location { get; set; }

    /// <summary>Detectors only: how they are powered or installed.</summary>
    public SafetyDetectorType? DetectorType { get; set; }

    /// <summary>
    /// Last periodic check (extinguishers), last test (detectors), date of the declaration of conformity (systems) or
    /// of the BDSR declaration. Used for reminders, never presented as a legal deadline.
    /// </summary>
    public DateOnly? CheckedOn { get; set; }

    /// <summary>Detectors only: end of life of the sensor from the manufacturer's manual.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Optional proof: a document or photo of the property (private bucket, FD-07).</summary>
    public Guid? EvidenceDocumentId { get; set; }
    public virtual PropertyDocument? EvidenceDocument { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string? UpdatedBy { get; set; }
}

/// <summary>Items of the checklist (stored as integers; ids of sicurezza.md in brackets).</summary>
public enum SafetyItemCode
{
    /// <summary>[SC-02] Portable extinguishers: always required, never "not applicable".</summary>
    FireExtinguishers = 1,

    /// <summary>[SC-04] Combustible gas detector: required unless the unit has no gas and no combustion.</summary>
    GasDetector = 2,

    /// <summary>[SC-05] Carbon monoxide detector: same condition as <see cref="GasDetector"/>.</summary>
    CoDetector = 3,

    /// <summary>[SC-06] Systems compliant with state and regional rules: required only for business management.</summary>
    SystemsCompliance = 4,

    /// <summary>[SC-07] Declaration of the art. 13-ter c. 7 requirements made in BDSR with the CIN request or update.</summary>
    BdsrDeclaration = 5,

    /// <summary>[SC-F1] Smoke detector: recommended, not required by art. 13-ter.</summary>
    SmokeDetector = 6,

    /// <summary>[SC-F2] Emergency instructions and numbers for guests: recommended, not required by art. 13-ter.</summary>
    EmergencyInstructions = 7,
}

/// <summary>Answer stored for an item (as an integer).</summary>
public enum SafetyItemAnswer
{
    Present = 1,
    Missing = 2,

    /// <summary>Imported from the old checklist (schema 1): the host must answer again. Never accepted from clients.</summary>
    ToReview = 3,
}

/// <summary>SC-03: combustion appliances of the unit (stored as integers).</summary>
public enum CombustionAppliance
{
    Boiler = 1,
    WaterHeater = 2,
    GasHob = 3,
    Stove = 4,
    Fireplace = 5,
    Other = 6,
}

/// <summary>SC-04/SC-05: kind of detector (battery, mains, fixed system under D.M. 37/2008).</summary>
public enum SafetyDetectorType
{
    Battery = 1,
    Mains = 2,
    FixedSystem = 3,
}
