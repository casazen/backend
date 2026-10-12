using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Demo;

/// <summary>
/// A safety checklist that <see cref="SafetyChecklistRules.Evaluate"/> accepts: not a business, no gas and no
/// combustion (the detectors do not apply), one floor, one extinguisher, the BDSR declaration and the confirmation.
/// </summary>
public static class DemoSafetyChecklist
{
    public const string Actor = "demo-seed";

    public static void Apply(PropertySafetyChecklist checklist, DateTime utcNow)
    {
        checklist.SchemaVersion = SafetyChecklistRules.SchemaVersion;
        checklist.LegalBasis = SafetyChecklistRules.LegalBasis;
        checklist.Entrepreneurial = false;
        checklist.HasGasSupply = false;
        checklist.CombustionAppliances = [];
        checklist.FloorCount = 1;
        checklist.FloorAreasSqm = [80m];
        checklist.ConfirmedAt = utcNow;
        checklist.ConfirmedBy = Actor;
        checklist.ConfirmedTextVersion = SafetyChecklistRules.DeclarationTextVersion;
        checklist.UpdatedAt = utcNow;
        checklist.UpdatedBy = Actor;
    }

    public static IReadOnlyList<PropertySafetyChecklistItem> Items(Guid orgId, DateOnly checkedOn) =>
    [
        Item(orgId, SafetyItemCode.FireExtinguishers, 1, "ingresso", null, checkedOn),
        Item(orgId, SafetyItemCode.BdsrDeclaration, null, null, null, checkedOn),
        Item(orgId, SafetyItemCode.SmokeDetector, 1, "corridoio", SafetyDetectorType.Battery, checkedOn),
        Item(orgId, SafetyItemCode.EmergencyInstructions, null, "ingresso", null, checkedOn),
    ];

    private static PropertySafetyChecklistItem Item(
        Guid orgId,
        SafetyItemCode code,
        int? quantity,
        string? location,
        SafetyDetectorType? detectorType,
        DateOnly checkedOn) =>
        new()
        {
            OrgId = orgId,
            Code = code,
            Answer = SafetyItemAnswer.Present,
            Quantity = quantity,
            Location = location,
            DetectorType = detectorType,
            CheckedOn = checkedOn,
        };
}
