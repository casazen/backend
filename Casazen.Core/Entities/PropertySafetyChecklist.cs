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

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public int SchemaVersion { get; set; }

    [MaxLength(100)]
    public string LegalBasis { get; set; } = string.Empty;

    /// <summary>SC-01: the host runs the rentals as a business (VAT number, company or presumption by number of flats).</summary>
    public bool? Entrepreneurial { get; set; }

    public bool? HasGasSupply { get; set; }

    public List<CombustionAppliance>? CombustionAppliances { get; set; }

    public int? FloorCount { get; set; }

    public List<decimal>? FloorAreasSqm { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(255)]
    public string? ConfirmedBy { get; set; }

    [MaxLength(50)]
    public string? ConfirmedTextVersion { get; set; }

    public string? LegacyChecklistJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string? UpdatedBy { get; set; }

    public virtual ICollection<PropertySafetyChecklistItem> Items { get; set; } = new List<PropertySafetyChecklistItem>();
}

[Table("PropertySafetyChecklistItems")]
public class PropertySafetyChecklistItem : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Checklist))]
    public Guid ChecklistId { get; set; }
    public virtual PropertySafetyChecklist Checklist { get; set; } = null!;

    public SafetyItemCode Code { get; set; }

    public SafetyItemAnswer? Answer { get; set; }

    public int? Quantity { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    public SafetyDetectorType? DetectorType { get; set; }

    public DateOnly? CheckedOn { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public Guid? EvidenceDocumentId { get; set; }
    public virtual PropertyDocument? EvidenceDocument { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string? UpdatedBy { get; set; }
}

public enum SafetyItemCode
{
    FireExtinguishers = 1,

    /// <summary>[SC-04] Combustible gas detector: required unless the unit has no gas and no combustion.</summary>
    GasDetector = 2,

    CoDetector = 3,

    SystemsCompliance = 4,

    /// <summary>[SC-07] Declaration of the art. 13-ter c. 7 requirements made in BDSR with the CIN request or update.</summary>
    BdsrDeclaration = 5,

    SmokeDetector = 6,

    /// <summary>[SC-F2] Emergency instructions and numbers for guests: recommended, not required by art. 13-ter.</summary>
    EmergencyInstructions = 7,
}

public enum SafetyItemAnswer
{
    Present = 1,
    Missing = 2,

    ToReview = 3,
}

public enum CombustionAppliance
{
    Boiler = 1,
    WaterHeater = 2,
    GasHob = 3,
    Stove = 4,
    Fireplace = 5,
    Other = 6,
}

public enum SafetyDetectorType
{
    Battery = 1,
    Mains = 2,
    FixedSystem = 3,
}
