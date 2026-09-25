using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>What the host declares about the unit (SC-01, SC-03, floors of SC-02).</summary>
public sealed record SafetyChecklistFactsInput(
    bool? Entrepreneurial,
    bool? HasGasSupply,
    IReadOnlyList<CombustionAppliance>? CombustionAppliances,
    int? FloorCount,
    IReadOnlyList<decimal>? FloorAreasSqm);

/// <summary>Answer to one item. <see cref="Answer"/> null keeps an imported "to review" answer, else clears it.</summary>
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

/// <summary>Whole checklist as saved by the host; <see cref="Confirm"/> is the final confirmation (SC-08).</summary>
public sealed record SafetyChecklistInput(
    SafetyChecklistFactsInput Facts,
    IReadOnlyList<SafetyChecklistItemInput> Items,
    bool Confirm);

/// <summary>Saved checklist (null when the host has not saved one yet) and its evaluation.</summary>
public sealed record SafetyChecklistView(
    PropertySafetyChecklist? Checklist,
    SafetyChecklistEvaluation Evaluation,
    IReadOnlyDictionary<Guid, string> EvidenceFileNames);

/// <summary>
/// Safety checklist of a short-stay property (CO-07). Callers authorize the property first (TN-3); the tenant filter
/// scopes every read. Invalid input raises <see cref="Exceptions.DomainRuleException"/> (422) with a stable code.
/// </summary>
public interface IPropertySafetyChecklistService
{
    Task<SafetyChecklistView> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task<SafetyChecklistView> SaveAsync(
        Guid propertyId,
        string userId,
        SafetyChecklistInput input,
        CancellationToken cancellationToken = default);
}
