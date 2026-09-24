using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface ICedolareAdvisoryService
{
    /// <summary>
    /// Advisory of the lease, or <c>null</c> when it does not exist in the caller's org (tenant filter). No ownership
    /// check: the caller authorizes the lease first (TN-3).
    /// </summary>
    Task<CedolareAdvisoryResult?> EvaluateAsync(Guid leaseId, CancellationToken cancellationToken = default);
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
    /// <summary>RLI prefill of the lease, or <c>null</c> when it is not visible. The caller authorizes the lease first (TN-3).</summary>
    Task<RliExportResult?> ExportAsync(Guid leaseId, CancellationToken cancellationToken = default);
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

/// <param name="RegistrationDeadline">
/// <c>min(stipula, start) + 30</c> days (LT-04, <c>RliRegistrationDeadline</c>); null while it is to be determined.
/// </param>
/// <param name="DaysRemaining">Days to the deadline on the Rome calendar: 0 on the deadline day, negative after it; null with no deadline.</param>
/// <param name="ProviderFilingAvailable">
/// The provider path exists (<c>Features:RliProvider</c> on and a configured provider): only then the delega item is
/// listed and the frontend offers "submit through the provider". Otherwise the landlord registers manually (LT-01).
/// </param>
public record RliChecklistResult(
    DateTime? RegistrationDeadline,
    int? DaysRemaining,
    string TosVersion,
    string AttestationText,
    bool ProviderFilingAvailable,
    IReadOnlyList<RliChecklistItem> Items);

/// <summary>
/// A checklist item. <paramref name="Done"/> is true only when the step really happened (LT-01: the registration item
/// only with the registration recorded and its receipt); <paramref name="Failed"/> marks a step whose last attempt
/// failed, so the UI shows the error and the way forward instead of a tick.
/// </summary>
public record RliChecklistItem(string Key, bool Done, bool Failed = false);

/// <summary>Stable keys of the RLI checklist items (labels: <c>RliChecklist_{key}</c> in the API resources).</summary>
public static class RliChecklistKeys
{
    public const string ContractSigned = "contract_signed";
    public const string DelegaCaptured = "delega_captured";
    public const string RliExported = "rli_exported";
    public const string RliRegistered = "rli_registered";
    public const string QuesturaExtraEu = "questura_extra_eu";

    public static IReadOnlyList<string> All { get; } =
        [ContractSigned, DelegaCaptured, RliExported, RliRegistered, QuesturaExtraEu];
}

public record RegistrationAuthorizationRequest(string TosVersion, bool AttestationAccepted);
