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
    bool IsProviderSigningAvailable { get; }

    Task<byte[]> GenerateContractForSignatureAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseContract> DeclareOfflineSignatureAsync(
        Guid leaseId,
        string userId,
        OfflineSignatureDeclaration declaration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a contract signed outside CasaZen, with the model of the owner (LR-13, decisions LR-D2 and LR-D28): the only way a
    /// contract gets to Signed without a template of ours, so the gate of the models (<see cref="ILeaseTemplateService.EnsureFinalContractAvailable"/>)
    /// stays for every other path. Only a draft of origin <see cref="Entities.Enums.LeaseOrigin.Imported"/> whose review has nothing
    /// blocking: stores the signed PDF in the private bucket (and a copy as the <c>Contract</c> document of the lease, so the
    /// Documents section shows it), records the stipula, marks every party signed and the lease Signed, and writes the events
    /// <c>AllPartiesSigned</c> (payload <c>imported</c>) and <c>Imported</c>. The registration is declared afterwards, with the manual
    /// declaration that already exists. 409 <c>lease_not_draft</c>, 422 <c>lease_import_not_imported_origin</c>,
    /// 422 <c>lease_review_blocked</c>, 422 <c>lease_signed_contract_invalid</c>, 422 <c>lease_stipula_date_in_future</c>.
    /// </summary>
    Task<LeaseContract> ImportSignedContractAsync(
        Guid leaseId,
        string userId,
        ImportedContractDeclaration declaration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same import with the signed PDF the owner has <b>already uploaded</b> to the draft (NL-DOCS): the contract document of kind
    /// <c>Contract</c> of this lease, named by id, instead of a file sent a second time. The document stays the one in the Documents
    /// section; the signed copy of the lease is a copy of its bytes with a key of its own (never one key for two owners, see
    /// <see cref="ImportSignedContractAsync"/>). 404 <c>document_not_found</c> for a document that is not a contract document of this
    /// lease (another lease, another org, another kind); the rest as <see cref="ImportSignedContractAsync"/>.
    /// </summary>
    Task<LeaseContract> ImportSignedContractFromDocumentAsync(
        Guid leaseId,
        string userId,
        ImportedDocumentDeclaration declaration,
        CancellationToken cancellationToken = default);

    Task<LeaseContract> DeclareStipulaAsync(
        Guid leaseId,
        string userId,
        DateTime stipulaDate,
        CancellationToken cancellationToken = default);

    Task<SignedContractFile> OpenSignedContractAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LeaseSignerDto>> GetSignersAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<LeaseSigningStateDto> GetSigningStateAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LeaseSignerDto>> InitiateProviderSigningAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task HandleProviderEventAsync(string payload, CancellationToken cancellationToken = default);
}

public sealed record OfflineSignatureDeclaration(DateTime StipulaDate, Stream SignedContract, long SignedContractLength);

public sealed record ImportedContractDeclaration(DateTime StipulaDate, Stream SignedContract, long SignedContractLength, string? FileName);

public sealed record ImportedDocumentDeclaration(DateTime StipulaDate, Guid DocumentId);

public sealed record SignedContractFile(Stream Content, string FileName);

public static class LeaseSigningLimits
{
    public const long MaxSignedContractBytes = 20 * 1024 * 1024;
}

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

    public const string ImportNotImported = "lease_import_not_imported_origin";
}
