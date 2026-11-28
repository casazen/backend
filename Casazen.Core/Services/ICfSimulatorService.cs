using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public sealed record CfSimulatorLinkParameters(
    string? Partner,
    string? LeaseId,
    string? ExpiresAt,
    string? Signature,
    string? Comune,
    string? Provincia);

public sealed record CfSimulatorPracticeView(
    string PracticeId,
    CfPracticeStatus Status,
    DateTime StatusChangedAt,
    DateTime SubmittedAt,
    string? Comune,
    bool? ComuneDiffersFromLink,
    string? CloseReason);

public sealed record CfSimulatorLinkResult(
    bool Valid,
    string? Reason,
    Guid? LeaseId,
    string? Comune,
    string? Provincia,
    IReadOnlyList<CfSimulatorPracticeView> Practices);

public sealed record CfSimulatorCreatedPractice(CfSimulatorPracticeView Practice, bool Linked, string ControlToken);

public enum CfSimulatorDocumentKind
{
    Contract,

    Attestation,

    Receipt,
}

public sealed record CfSimulatorDocument(byte[] Content, string FileName);

/// <summary>
/// The simulated module and status API of ConcordatoFacile (CF-SIM), for the test environment only: it plays what ConcordatoFacile does,
/// verifying the signed link as the specification v0.2 describes, taking a practice in, letting a person move it through the states and
/// delivering sample documents. Reached through <c>api/cf-simulator/*</c>, which answers 404 unless <c>ConcordatoFacile:FormMode</c> is
/// <c>Simulated</c>.
/// </summary>
public interface ICfSimulatorService
{
    Task<CfSimulatorLinkResult> CheckLinkAsync(CfSimulatorLinkParameters link, CancellationToken cancellationToken = default);

    Task<CfSimulatorCreatedPractice> CreatePracticeAsync(CfSimulatorLinkParameters link, CancellationToken cancellationToken = default);

    Task<CfSimulatorPracticeView?> SetStatusAsync(
        string practiceId,
        string? controlToken,
        CfPracticeStatus status,
        string? closeReason,
        CancellationToken cancellationToken = default);

    Task<CfSimulatorDocument?> RenderDocumentAsync(
        string practiceId,
        string? controlToken,
        CfSimulatorDocumentKind kind,
        CancellationToken cancellationToken = default);
}
