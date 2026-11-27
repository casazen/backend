using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

public enum SafetyItemRequirement
{
    /// <summary>Required by art. 13-ter c. 7: it blocks the activation until it is present.</summary>
    Required,

    /// <summary>Recommended, not required by art. 13-ter (smoke detector, emergency instructions): never blocks.</summary>
    Optional,

    NotApplicable,

    Undetermined,
}

public enum SafetyItemStatus
{
    Present,
    Missing,
    NotAnswered,

    ToReview,
    NotApplicable,
}

public enum SafetyNotApplicableReason
{
    NoGasNoCombustion = 1,

    /// <summary><c>non_imprenditoriale</c>: the rentals are not run as a business (art. 13-ter c. 7, first sentence).</summary>
    NotEntrepreneurial = 2,
}

/// <summary>Blocker or warning of the checklist: stable snake_case <see cref="Code"/> and localized message key.</summary>
public sealed record SafetyChecklistIssue(string Code, string MessageKey, IReadOnlyList<object> MessageArgs)
{
    public SafetyChecklistIssue(string code, string messageKey)
        : this(code, messageKey, [])
    {
    }
}

public sealed record SafetyItemEvaluation(
    SafetyItemCode Code,
    SafetyItemRequirement Requirement,
    SafetyItemStatus Status,
    SafetyNotApplicableReason? NotApplicableReason);

public sealed record SafetyChecklistEvaluation(
    IReadOnlyList<SafetyItemEvaluation> Items,
    int? MinimumExtinguishers,
    IReadOnlyList<SafetyChecklistIssue> Blockers,
    IReadOnlyList<SafetyChecklistIssue> Warnings)
{
    public bool IsComplete => Blockers.Count == 0;
}

/// <summary>
/// Single source of truth of the safety checklist (CO-07, A5-21) under D.L. 145/2023 art. 13-ter, c. 7, as verified by
/// RS-3 in <c>.claude/context/regulations/sicurezza.md</c> («Obblighi verificati (2026-09)», «Proposta di checklist per
/// CO-07»). Only the required items block the activation:
/// <list type="bullet">
/// <item>portable extinguishers, always, at least <see cref="MinimumExtinguishers"/>;</item>
/// <item>combustible gas and carbon monoxide detectors, "not applicable" only when the unit has no gas system and no
/// combustion appliance (FAQ of the Ministero del Turismo: both conditions);</item>
/// <item>systems compliant with state and regional rules, only for business management;</item>
/// <item>the declaration made in BDSR and the host's final confirmation.</item>
/// </list>
/// Smoke detector and emergency instructions are recommended and never block. The open legal questions are in
/// sicurezza.md, «Dubbi per il legale»: this class implements the prudent proposal of RS-3 and decides none of them.
/// </summary>
public static class SafetyChecklistRules
{
    public const int SchemaVersion = 2;

    public const int LegacySchemaVersion = 1;

    public const string LegalBasis = "DL145/2023 art.13-ter, RS-3 2026-09";

    public const string DeclarationTextVersion = "2026-09-v1";

    /// <summary>Art. 13-ter c. 7: one extinguisher every 200 m² of floor or fraction, at least one per floor.</summary>
    public const decimal FloorAreaPerExtinguisherSqm = 200m;

    public const int MaxFloors = 20;
    public const decimal MaxFloorAreaSqm = 10_000m;
    public const int MaxQuantity = 100;

    public static readonly IReadOnlyList<SafetyItemCode> Items =
    [
        SafetyItemCode.FireExtinguishers,
        SafetyItemCode.GasDetector,
        SafetyItemCode.CoDetector,
        SafetyItemCode.SystemsCompliance,
        SafetyItemCode.BdsrDeclaration,
        SafetyItemCode.SmokeDetector,
        SafetyItemCode.EmergencyInstructions,
    ];

    public static bool IsDetector(SafetyItemCode code) =>
        code is SafetyItemCode.GasDetector or SafetyItemCode.CoDetector or SafetyItemCode.SmokeDetector;

    public static bool IsDetectorExempt(bool? hasGasSupply, IReadOnlyCollection<CombustionAppliance>? appliances) =>
        hasGasSupply == false && appliances is { Count: 0 };

    public static int? MinimumExtinguishers(int? floorCount, IReadOnlyList<decimal>? floorAreasSqm)
    {
        if (floorCount is not > 0)
            return null;

        if (floorAreasSqm is null || floorAreasSqm.Count != floorCount)
            return floorCount;

        return floorAreasSqm.Sum(area => Math.Max(1, (int)Math.Ceiling(area / FloorAreaPerExtinguisherSqm)));
    }

    public static (SafetyItemRequirement Requirement, SafetyNotApplicableReason? Reason) RequirementOf(
        SafetyItemCode code,
        PropertySafetyChecklist? checklist)
    {
        switch (code)
        {
            case SafetyItemCode.FireExtinguishers:
            case SafetyItemCode.BdsrDeclaration:
                return (SafetyItemRequirement.Required, null);

            case SafetyItemCode.GasDetector:
            case SafetyItemCode.CoDetector:
                if (IsDetectorExempt(checklist?.HasGasSupply, checklist?.CombustionAppliances))
                    return (SafetyItemRequirement.NotApplicable, SafetyNotApplicableReason.NoGasNoCombustion);
                return checklist?.HasGasSupply == true || checklist?.CombustionAppliances is { Count: > 0 }
                    ? (SafetyItemRequirement.Required, null)
                    : (SafetyItemRequirement.Undetermined, null);

            case SafetyItemCode.SystemsCompliance:
                return checklist?.Entrepreneurial switch
                {
                    true => (SafetyItemRequirement.Required, null),
                    false => (SafetyItemRequirement.NotApplicable, SafetyNotApplicableReason.NotEntrepreneurial),
                    null => (SafetyItemRequirement.Undetermined, null),
                };

            default:
                return (SafetyItemRequirement.Optional, null);
        }
    }

    public static SafetyChecklistEvaluation Evaluate(PropertySafetyChecklist? checklist)
    {
        var blockers = new List<SafetyChecklistIssue>();
        var warnings = new List<SafetyChecklistIssue>();

        if (checklist?.Entrepreneurial is null)
            blockers.Add(new("safety_entrepreneurial_unanswered", "SafetyEntrepreneurialUnanswered"));

        var detectorsDecided = IsDetectorExempt(checklist?.HasGasSupply, checklist?.CombustionAppliances)
            || checklist?.HasGasSupply == true
            || checklist?.CombustionAppliances is { Count: > 0 };
        if (!detectorsDecided)
            blockers.Add(new("safety_gas_unanswered", "SafetyGasUnanswered"));

        if (checklist?.FloorCount is not > 0)
            blockers.Add(new("safety_floors_unanswered", "SafetyFloorsUnanswered"));
        else if (checklist.FloorAreasSqm is null || checklist.FloorAreasSqm.Count != checklist.FloorCount)
            warnings.Add(new("safety_floor_areas_missing", "SafetyFloorAreasMissing"));

        var minimum = MinimumExtinguishers(checklist?.FloorCount, checklist?.FloorAreasSqm);
        var answers = (checklist?.Items ?? []).ToDictionary(i => i.Code);

        var items = new List<SafetyItemEvaluation>();
        foreach (var code in Items)
        {
            var (requirement, reason) = RequirementOf(code, checklist);
            answers.TryGetValue(code, out var answer);
            var status = requirement == SafetyItemRequirement.NotApplicable
                ? SafetyItemStatus.NotApplicable
                : answer?.Answer switch
                {
                    SafetyItemAnswer.Present => SafetyItemStatus.Present,
                    SafetyItemAnswer.Missing => SafetyItemStatus.Missing,
                    SafetyItemAnswer.ToReview => SafetyItemStatus.ToReview,
                    _ => SafetyItemStatus.NotAnswered,
                };
            items.Add(new SafetyItemEvaluation(code, requirement, status, reason));

            if (requirement == SafetyItemRequirement.Required)
                AddItemBlocker(blockers, code, status, answer?.Quantity, minimum);
        }

        if (checklist?.ConfirmedAt is null || checklist.ConfirmedTextVersion != DeclarationTextVersion)
            blockers.Add(new("safety_confirmation_missing", "SafetyConfirmationMissing"));

        return new SafetyChecklistEvaluation(items, minimum, blockers, warnings);
    }

    private static void AddItemBlocker(
        List<SafetyChecklistIssue> blockers,
        SafetyItemCode code,
        SafetyItemStatus status,
        int? quantity,
        int? minimum)
    {
        if (status == SafetyItemStatus.Present)
        {
            if (code == SafetyItemCode.FireExtinguishers && minimum is { } min && (quantity ?? 0) < min)
            {
                blockers.Add(new(
                    "safety_extinguishers_below_minimum",
                    "SafetyExtinguishersBelowMinimum",
                    [quantity ?? 0, min]));
            }

            return;
        }

        blockers.Add((code, status) switch
        {
            (SafetyItemCode.FireExtinguishers, SafetyItemStatus.ToReview) =>
                new("safety_extinguishers_review", "SafetyExtinguishersReview"),
            (SafetyItemCode.FireExtinguishers, _) => new("safety_extinguishers_missing", "SafetyExtinguishersMissing"),
            (SafetyItemCode.GasDetector, _) => new("safety_gas_detector_missing", "SafetyGasDetectorMissing"),
            (SafetyItemCode.CoDetector, _) => new("safety_co_detector_missing", "SafetyCoDetectorMissing"),
            (SafetyItemCode.SystemsCompliance, SafetyItemStatus.ToReview) =>
                new("safety_systems_compliance_review", "SafetySystemsComplianceReview"),
            (SafetyItemCode.SystemsCompliance, _) =>
                new("safety_systems_compliance_missing", "SafetySystemsComplianceMissing"),
            _ => new("safety_bdsr_declaration_missing", "SafetyBdsrDeclarationMissing"),
        });
    }
}
