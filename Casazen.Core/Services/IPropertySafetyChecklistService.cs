using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

public sealed record SafetyChecklistFactsInput(
    bool? Entrepreneurial,
    bool? HasGasSupply,
    IReadOnlyList<CombustionAppliance>? CombustionAppliances,
    int? FloorCount,
    IReadOnlyList<decimal>? FloorAreasSqm);

public sealed record SafetyChecklistItemInput(
    SafetyItemCode Code,
    SafetyItemAnswer? Answer,
    int? Quantity,
    string? Location,
    SafetyDetectorType? DetectorType,
    DateOnly? CheckedOn,
    DateOnly? ExpiresOn,
    Guid? EvidenceDocumentId,
    string? Notes);

public sealed record SafetyChecklistInput(
    SafetyChecklistFactsInput Facts,
    IReadOnlyList<SafetyChecklistItemInput> Items,
    bool Confirm);

public sealed record SafetyChecklistView(
    PropertySafetyChecklist? Checklist,
    SafetyChecklistEvaluation Evaluation,
    IReadOnlyDictionary<Guid, string> EvidenceFileNames);

public interface IPropertySafetyChecklistService
{
    Task<SafetyChecklistView> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task<SafetyChecklistView> SaveAsync(
        Guid propertyId,
        string userId,
        SafetyChecklistInput input,
        CancellationToken cancellationToken = default);
}
