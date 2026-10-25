using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The parameters of the link to the module of ConcordatoFacile (specification v0.2 §2), exactly as they were received: the signature is
/// verified on the text that arrived, before any normalisation.
/// </summary>
public sealed record CfSimulatorLinkParameters(
    string? Partner,
    string? LeaseId,
    string? ExpiresAt,
    string? Signature,
    string? Comune,
    string? Provincia);

/// <summary>A practice as the status API shows it (v0.2 §4): number, state, dates, municipality and, when closed, why.</summary>
public sealed record CfSimulatorPracticeView(
    string PracticeId,
    CfPracticeStatus Status,
    DateTime StatusChangedAt,
    DateTime SubmittedAt,
    string? Comune,
    bool? ComuneDiffersFromLink,
    string? CloseReason);

/// <summary>
/// What the module learns from its link: whether the signature and the expiry are good, the lease and the municipality it names and, only for
/// a good link, the practices already born from the contract. A link that is not good is not an error: the module can be filled in all the
/// same and the practice arrives without a lease (v0.2 §1).
/// </summary>
/// <param name="Reason">Why the link is not good: <c>missing_parameters</c>, <c>partner_invalid</c>, <c>lease_id_invalid</c>,
/// <c>expires_at_invalid</c>, <c>signature_invalid</c>, <c>expired</c>, <c>too_far</c> or <c>secret_not_configured</c>.</param>
public sealed record CfSimulatorLinkResult(
    bool Valid,
    string? Reason,
    Guid? LeaseId,
    string? Comune,
    string? Provincia,
    IReadOnlyList<CfSimulatorPracticeView> Practices);

/// <summary>A practice just born in the simulated module, with the control token that only this answer shows.</summary>
public sealed record CfSimulatorCreatedPractice(CfSimulatorPracticeView Practice, bool Linked, string ControlToken);

/// <summary>The sample documents a practice delivers, as ConcordatoFacile sends them by e-mail.</summary>
public enum CfSimulatorDocumentKind
{
    /// <summary>The contract, readable by the local reading of the lease documents.</summary>
    Contract,

    /// <summary>The attestation of conformity (archived, not read).</summary>
    Attestation,

    /// <summary>The receipt of the registration (archived, not read).</summary>
    Receipt,
}

/// <summary>A generated sample PDF.</summary>
public sealed record CfSimulatorDocument(byte[] Content, string FileName);

/// <summary>
/// The simulated module and status API of ConcordatoFacile (CF-SIM), for the test environment only: it plays what ConcordatoFacile does,
/// verifying the signed link as the specification v0.2 describes, taking a practice in, letting a person move it through the states and
/// delivering sample documents. Reached through <c>api/cf-simulator/*</c>, which answers 404 unless <c>ConcordatoFacile:FormMode</c> is
/// <c>Simulated</c>.
/// </summary>
public interface ICfSimulatorService
{
    /// <summary>Verifies the link (the signature on the text received, the expiry within 2 minutes of grace and 60 minutes ahead, the partner).</summary>
    Task<CfSimulatorLinkResult> CheckLinkAsync(CfSimulatorLinkParameters link, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes in a practice (state <c>Received</c>): bound to the lease only when the link is good. Another practice of the same lease that is
    /// not closed is closed as <c>ClosedWithoutAttestation</c> with <c>duplicate</c> (v0.2 §6).
    /// </summary>
    Task<CfSimulatorCreatedPractice> CreatePracticeAsync(CfSimulatorLinkParameters link, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the state of a practice (any state, <c>StatusChangedAt</c> now). Null when the practice does not exist or the control token is
    /// not its own: the two cases are not told apart.
    /// </summary>
    Task<CfSimulatorPracticeView?> SetStatusAsync(
        string practiceId,
        string? controlToken,
        CfPracticeStatus status,
        string? closeReason,
        CancellationToken cancellationToken = default);

    /// <summary>A sample document of a practice, null when the practice does not exist or the control token is not its own.</summary>
    Task<CfSimulatorDocument?> RenderDocumentAsync(
        string practiceId,
        string? controlToken,
        CfSimulatorDocumentKind kind,
        CancellationToken cancellationToken = default);
}
