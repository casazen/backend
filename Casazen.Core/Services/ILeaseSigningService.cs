using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Signature of a lease contract (LT-02: A7-02, A7-16, A7-20; decision D15). Two paths:
/// <list type="bullet">
/// <item><b>offline</b> (default, always available): the landlord downloads the final contract (only from an approved
/// template, LT-03), has it signed by every party on paper or with their own digital signature, uploads the signed PDF
/// and declares the stipula date; only then the lease is Signed and the RLI deadline is fixed (LT-04);</item>
/// <item><b>provider</b>: only when <see cref="IsProviderSigningAvailable"/>; the lease is Signed only when the provider
/// reports every signature and the signed PDF is copied into the private bucket.</item>
/// </list>
/// Every state change is one database transaction under a row lock on the lease. The caller authorizes the lease
/// (TN-3) before calling; the service never checks roles.
/// </summary>
public interface ILeaseSigningService
{
    /// <summary><c>Features:ESignProvider</c> on and a configured provider (<see cref="ESignProviderSigning"/>).</summary>
    bool IsProviderSigningAvailable { get; }

    /// <summary>
    /// The final contract PDF to be signed (not a preview). 422 <c>contract_template_not_approved</c> /
    /// <c>contract_data_missing</c> from the template gate (LT-03); 409 <c>lease_already_signed</c> once every party signed.
    /// </summary>
    Task<byte[]> GenerateContractForSignatureAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Offline signature: stores the PDF signed by every party in the private bucket, records the stipula
    /// (<see cref="LeaseContract.RecordStipula"/>), marks every party signed and the lease Signed.
    /// </summary>
    Task<LeaseContract> DeclareOfflineSignatureAsync(
        Guid leaseId,
        string userId,
        OfflineSignatureDeclaration declaration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stipula date of a lease already signed whose signature was never recorded (older leases): fixes the RLI
    /// deadline. 409 <c>lease_stipula_already_recorded</c> when the lease has one.
    /// </summary>
    Task<LeaseContract> DeclareStipulaAsync(
        Guid leaseId,
        string userId,
        DateTime stipulaDate,
        CancellationToken cancellationToken = default);

    /// <summary>The contract signed by every party, from the private bucket. 404 <c>lease_signed_contract_not_available</c>.</summary>
    Task<SignedContractFile> OpenSignedContractAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>One entry per party: the persisted signer, or the offline state when no signature path started yet.</summary>
    Task<IReadOnlyList<LeaseSignerDto>> GetSignersAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The signature panel: the signers (<see cref="GetSignersAsync"/>), whether the provider path exists and whether
    /// the final contract to sign can be downloaded now (template gate, LT-03), with the reason when it cannot.
    /// </summary>
    Task<LeaseSigningStateDto> GetSigningStateAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Provider path: sends the final contract to the provider, persists the signers with their links and moves the
    /// lease to AwaitingSignature. 409 <c>esign_provider_unavailable</c> without the provider path.
    /// </summary>
    Task<IReadOnlyList<LeaseSignerDto>> InitiateProviderSigningAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A provider webhook event (background job, signature already verified). Applied only to a lease AwaitingSignature
    /// or PartiallySigned: a replayed or late event never moves a Signed or Registered lease back.
    /// </summary>
    Task HandleProviderEventAsync(string payload, CancellationToken cancellationToken = default);
}

/// <summary>What the landlord declares for an offline signature: the stipula date and the PDF signed by every party.</summary>
public sealed record OfflineSignatureDeclaration(DateTime StipulaDate, Stream SignedContract, long SignedContractLength);

/// <summary>The stored signed contract; the caller disposes <see cref="Content"/>.</summary>
public sealed record SignedContractFile(Stream Content, string FileName);

/// <summary>Input limits of the offline signature.</summary>
public static class LeaseSigningLimits
{
    /// <summary>Largest signed contract accepted: a scan of a signed multi-page contract.</summary>
    public const long MaxSignedContractBytes = 20 * 1024 * 1024;
}

/// <summary>Error codes of the lease signature (ProblemDetails <c>code</c>, FD-05).</summary>
public static class LeaseSigningErrorCodes
{
    public const string ProviderUnavailable = "esign_provider_unavailable";
    public const string ProviderFailed = "esign_provider_failed";
    public const string WebhookNotConfigured = "esign_webhook_not_configured";
    public const string NotDraft = "lease_signing_not_draft";
    public const string AlreadySigned = "lease_already_signed";
    public const string SignedContractInvalid = "lease_signed_contract_invalid";
    public const string SignedContractNotAvailable = "lease_signed_contract_not_available";
    public const string StipulaDateInFuture = "lease_stipula_date_in_future";
    public const string StipulaAlreadyRecorded = "lease_stipula_already_recorded";
    public const string StipulaLeaseNotSigned = "lease_stipula_lease_not_signed";
}
