using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// RLI registration of a signed lease (LT-01, A7-01, A7-21, D15). Two paths:
/// <list type="bullet">
/// <item><b>manual</b> (default, always available): the landlord files on the official channel of the Agenzia delle
/// Entrate (RLI web, Entratel/Fisconline or an intermediary; for a contract made with the guided service, the service files it) and
/// declares the registration number or protocol, its date and the receipt PDF (a file sent now, or the receipt already among the
/// documents of the lease); only then the lease is Registered;</item>
/// <item><b>provider</b>: only when <see cref="IsProviderFilingAvailable"/>; the lease is "in progress" until the
/// provider returns the official receipt.</item>
/// </list>
/// Every state change is one database transaction (registration, delega, lease status and event together); a provider
/// failure leaves the registration Failed and the lease Signed, so both a retry and the manual path stay possible.
/// The caller authorizes the lease (TN-3) before calling.
/// </summary>
public interface IRliRegistrationService
{
    bool IsProviderFilingAvailable { get; }

    Task<LeaseRegistration> SubmitToProviderAsync(
        Guid leaseId,
        string ownerId,
        RegistrationAuthorizationRequest delega,
        CancellationToken cancellationToken = default);

    Task<LeaseRegistration> DeclareManualRegistrationAsync(
        Guid leaseId,
        string userId,
        ManualRegistrationDeclaration declaration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same declaration with the receipt the landlord already uploaded among the documents of the lease (CF-END), so it is not sent
    /// twice: a document of kind <see cref="LeaseDocumentKind.Registration"/> of <b>this</b> lease and of its org. Its bytes are read from the
    /// private bucket and checked as a file sent now (<c>%PDF-</c>, size), then stored under a key of their own: the document keeps its file
    /// and the registration has its own, never one key for two owners. Any other document (another lease's, another org's, another kind,
    /// none) is <c>NotFoundException</c> <c>document_not_found</c>, and nothing is read or written.
    /// </summary>
    Task<LeaseRegistration> DeclareManualRegistrationFromDocumentAsync(
        Guid leaseId,
        string userId,
        ManualRegistrationDocumentDeclaration declaration,
        CancellationToken cancellationToken = default);

    Task<RegistrationReceiptFile> OpenReceiptAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<ProviderSyncResult> SyncProviderRegistrationsAsync(CancellationToken cancellationToken = default);
}

public sealed record ManualRegistrationDeclaration(
    string RegistrationCode,
    DateTime RegistrationDate,
    Stream Receipt,
    long ReceiptLength);

public sealed record ManualRegistrationDocumentDeclaration(
    string RegistrationCode,
    DateTime RegistrationDate,
    Guid ReceiptDocumentId);

public sealed record RegistrationReceiptFile(Stream Content, string FileName);

public sealed record ProviderSyncResult(int Registered, int Failed, int StillInProgress, int Errors);

public static class RliRegistrationLimits
{
    /// <summary>Largest receipt accepted (the receipt of the Agenzia delle Entrate is a short PDF).</summary>
    public const long MaxReceiptBytes = 10 * 1024 * 1024;

    public const int MaxRegistrationCodeLength = 100;
}

public static class RliRegistrationErrorCodes
{
    public const string ProviderUnavailable = "rli_provider_unavailable";
    public const string ProviderFailed = "rli_provider_failed";
    public const string DelegaRequired = "rli_delega_required";
    public const string LeaseNotSigned = "rli_lease_not_signed";
    public const string SignedPdfMissing = "rli_signed_pdf_missing";
    public const string AlreadyRegistered = "rli_already_registered";
    public const string InProgress = "rli_registration_in_progress";
    public const string ReceiptInvalid = "rli_receipt_invalid";
    public const string RegistrationCodeInvalid = "rli_registration_code_invalid";
    public const string RegistrationDateInFuture = "rli_registration_date_in_future";
    public const string ReceiptNotAvailable = "rli_receipt_not_available";
}

public static class RliRegistrationFailureCodes
{
    public const string ProviderError = "provider_error";

    public const string ProviderRejected = "provider_rejected";

    public const string OutcomeUnknown = "provider_outcome_unknown";

    public const string SimulatedSubmission = "simulated_submission";
}
