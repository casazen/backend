using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface ICedolareAdvisoryService
{
    Task<CedolareAdvisoryResult?> EvaluateAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);
}

public record CedolareAdvisoryResult(
    FiscalRegime LeaseRegime,
    decimal AnnualRent,
    decimal CedolareRate,
    decimal CedolareEstimateEur,
    decimal RegistroRate,
    decimal RegistroEstimateEur,
    decimal BolloEur,
    string OrdinaryIrpefNote,
    string Disclaimer);

public interface IRliExportService
{
    Task<RliExportResult?> ExportAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);
}

public record RliExportResult(byte[] PdfBytes, string FileName);

public interface IRliChecklistService
{
    /// <summary>
    /// Checklist of <paramref name="lease"/>, loaded with its property, parties and registration and already
    /// authorized by the caller (TN-3). Items carry a stable key, never a text: the API localizes the labels.
    /// </summary>
    Task<RliChecklistResult> GetAsync(LeaseContract lease, CancellationToken cancellationToken = default);
}

public record RliChecklistResult(
    DateTime RegistrationDeadline,
    int DaysRemaining,
    string TosVersion,
    string AttestationText,
    IReadOnlyList<RliChecklistItem> Items);

public record RliChecklistItem(string Key, bool Done);

/// <summary>Stable keys of the RLI checklist items (labels: <c>RliChecklist_{key}</c> in the API resources).</summary>
public static class RliChecklistKeys
{
    public const string ContractSigned = "contract_signed";
    public const string DelegaCaptured = "delega_captured";
    public const string RliExported = "rli_exported";
    public const string RliSubmitted = "rli_submitted";
    public const string RliRegistered = "rli_registered";
    public const string QuesturaExtraEu = "questura_extra_eu";

    public static IReadOnlyList<string> All { get; } =
        [ContractSigned, DelegaCaptured, RliExported, RliSubmitted, RliRegistered, QuesturaExtraEu];
}

public record RegistrationAuthorizationRequest(string TosVersion, bool AttestationAccepted);
