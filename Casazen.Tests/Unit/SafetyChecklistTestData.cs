using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Tests.Unit;

/// <summary>Safety checklists (CO-07) shared by the unit and integration tests.</summary>
public static class SafetyChecklistTestData
{
    public static readonly DateTime ConfirmedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A complete checklist with no blocker: all-electric unit (no gas, no combustion appliance), not run as a
    /// business, one floor of 80 m² with one extinguisher, BDSR declaration made, confirmed with the current text.
    /// </summary>
    public static PropertySafetyChecklist CompleteAllElectric(Guid propertyId, Guid orgId)
    {
        var checklist = new PropertySafetyChecklist
        {
            OrgId = orgId,
            PropertyId = propertyId,
            SchemaVersion = SafetyChecklistRules.SchemaVersion,
            LegalBasis = SafetyChecklistRules.LegalBasis,
            Entrepreneurial = false,
            HasGasSupply = false,
            CombustionAppliances = [],
            FloorCount = 1,
            FloorAreasSqm = [80m],
            ConfirmedAt = ConfirmedAt,
            ConfirmedBy = "auth0|owner",
            ConfirmedTextVersion = SafetyChecklistRules.DeclarationTextVersion,
        };

        foreach (var code in SafetyChecklistRules.Items)
        {
            checklist.Items.Add(new PropertySafetyChecklistItem
            {
                OrgId = orgId,
                Code = code,
                Answer = code switch
                {
                    SafetyItemCode.FireExtinguishers or SafetyItemCode.BdsrDeclaration => SafetyItemAnswer.Present,
                    _ => null,
                },
                Quantity = code == SafetyItemCode.FireExtinguishers ? 1 : null,
            });
        }

        return checklist;
    }

    /// <summary>Input for an all-electric unit with one extinguisher and the BDSR declaration (see <see cref="CompleteAllElectric"/>).</summary>
    public static SafetyChecklistInput AllElectricInput(bool confirm = true) => new(
        new SafetyChecklistFactsInput(
            Entrepreneurial: false,
            HasGasSupply: false,
            CombustionAppliances: [],
            FloorCount: 1,
            FloorAreasSqm: [80m]),
        [
            Item(SafetyItemCode.FireExtinguishers, SafetyItemAnswer.Present, quantity: 1),
            Item(SafetyItemCode.BdsrDeclaration, SafetyItemAnswer.Present),
        ],
        confirm);

    public static SafetyChecklistItemInput Item(
        SafetyItemCode code,
        SafetyItemAnswer? answer,
        int? quantity = null,
        DateOnly? checkedOn = null,
        Guid? evidenceDocumentId = null) =>
        new(code, answer, quantity, null, null, checkedOn, null, evidenceDocumentId, null);
}
