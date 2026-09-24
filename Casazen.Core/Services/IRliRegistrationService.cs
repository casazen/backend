using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// RLI registration of a signed lease (LT-01, A7-01, A7-21, D15). Two paths:
/// <list type="bullet">
/// <item><b>manual</b> (default, always available): the landlord files on the official channel of the Agenzia delle
/// Entrate (RLI web, Entratel/Fisconline or an intermediary) and declares the registration number or protocol, its
/// date and the receipt PDF; only then the lease is Registered;</item>
/// <item><b>provider</b>: only when <see cref="IsProviderFilingAvailable"/>; the lease is "in progress" until the
/// provider returns the official receipt.</item>
/// </list>
/// Every state change is one database transaction (registration, delega, lease status and event together); a provider
/// failure leaves the registration Failed and the lease Signed, so both a retry and the manual path stay possible.
/// The caller authorizes the lease (TN-3) before calling.
/// </summary>
public interface IRliRegistrationService
{
    /// <summary><c>Features:RliProvider</c> on and a configured provider (<see cref="RliProviderFiling"/>).</summary>
    bool IsProviderFilingAvailable { get; }

    /// <summary>
    /// Records the owner's delega and submits the signed lease to the provider. Throws
    /// <see cref="LeaseRegistrationProviderException"/> after recording the failure when the provider fails.
    /// </summary>
    Task<LeaseRegistration> SubmitToProviderAsync(
        Guid leaseId,
        string ownerId,
        RegistrationAuthorizationRequest delega,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the receipt in the private bucket and marks the lease Registered with the declared data.</summary>
    Task<LeaseRegistration> DeclareManualRegistrationAsync(
        Guid leaseId,
        string userId,
        ManualRegistrationDeclaration declaration,
        CancellationToken cancellationToken = default);

    /// <summary>Receipt of a registered lease, from the private bucket.</summary>
    Task<RegistrationReceiptFile> OpenReceiptAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Provider polling (background job): confirms requests with a receipt, records failures and fails reservations
    /// left without an outcome. No-op when the provider path is not available.
    /// </summary>
    Task<ProviderSyncResult> SyncProviderRegistrationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What the landlord declares after filing: number or protocol, date and the receipt. The receipt must be a PDF
/// (checked on its content, not on the declared type or name) of at most 10 MB.
/// </summary>
public sealed record ManualRegistrationDeclaration(
    string RegistrationCode,
    DateTime RegistrationDate,
    Stream Receipt,
    long ReceiptLength);

/// <summary>The stored receipt; the caller disposes <see cref="Content"/>.</summary>
public sealed record RegistrationReceiptFile(Stream Content, string FileName);

/// <summary>Outcome counters of one provider sync run.</summary>
public sealed record ProviderSyncResult(int Registered, int Failed, int StillInProgress, int Errors);

/// <summary>Input limits of the manual registration.</summary>
public static class RliRegistrationLimits
{
    /// <summary>Largest receipt accepted (the receipt of the Agenzia delle Entrate is a short PDF).</summary>
    public const long MaxReceiptBytes = 10 * 1024 * 1024;

    /// <summary>Same limit as <c>LeaseRegistration.RegistrationCode</c>.</summary>
    public const int MaxRegistrationCodeLength = 100;
}

/// <summary>Error codes of the RLI registration (ProblemDetails <c>code</c>, FD-05).</summary>
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

/// <summary>Stable codes stored in <c>LeaseRegistration.FailureCode</c> (the frontend translates them).</summary>
public static class RliRegistrationFailureCodes
{
    /// <summary>The provider call failed (network, outage, credit, validation).</summary>
    public const string ProviderError = "provider_error";

    /// <summary>The provider closed the request without a registration.</summary>
    public const string ProviderRejected = "provider_rejected";

    /// <summary>
    /// The submission was interrupted before its outcome was recorded: the provider may or may not have the request.
    /// The landlord checks before filing again.
    /// </summary>
    public const string OutcomeUnknown = "provider_outcome_unknown";

    /// <summary>Submission made by the old simulated provider (before LT-01): nothing was filed.</summary>
    public const string SimulatedSubmission = "simulated_submission";
}
