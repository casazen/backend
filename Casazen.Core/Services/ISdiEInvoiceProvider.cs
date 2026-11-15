using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Status of the Italian electronic invoice (FatturaPA through the Sistema di Interscambio, SDI) of a
/// <see cref="PlatformInvoice"/> (PL-13, A1-08). Stripe does not issue FatturaPA invoices (fiscale.md S6): either a
/// configured <see cref="ISdiEInvoiceProvider"/> submits them, or the product owner issues them by hand. A status never
/// says "sent" without a provider's answer or an admin's declaration.
/// </summary>
public static class PlatformInvoiceSdiStatuses
{
    /// <summary>No SDI provider is configured: the e-invoice must be issued manually (runbook billing-tax.md).</summary>
    public const string ManualRequired = "manual_required";

    /// <summary>A provider is configured and the submission has not been attempted yet.</summary>
    public const string Pending = "pending";

    /// <summary>The provider accepted the invoice (<see cref="PlatformInvoice.SdiTransmissionId"/> is its id).</summary>
    public const string Submitted = "submitted";

    /// <summary>The provider refused the invoice or could not be reached (<see cref="PlatformInvoice.SdiError"/>).</summary>
    public const string Failed = "failed";

    /// <summary>A platform admin declared the e-invoice issued by hand, with its reference.</summary>
    public const string ManualIssued = "manual_issued";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ManualRequired, Pending, Submitted, Failed, ManualIssued,
    };
}

/// <summary>Outcome of <see cref="ISdiEInvoiceProvider.SubmitAsync"/> and of <see cref="ISdiEInvoiceProvider.SubmitDocumentAsync"/>.</summary>
/// <param name="Accepted">True only when the provider accepted the invoice and returned its id.</param>
/// <param name="TransmissionId">The provider's id of the submission, when accepted.</param>
/// <param name="Error">
/// Why the submission failed (no PII), when not accepted: for a fiscal document a stable code of <see cref="SdiSubmissionErrors"/> (at most 20 characters: it is stored on the document).
/// </param>
/// <param name="Detail">A short note on the refusal (the code and the status of the provider's answer, never the file or a value of it), when the provider gave one.</param>
public sealed record SdiSubmissionResult(bool Accepted, string? TransmissionId, string? Error, string? Detail = null)
{
    public static SdiSubmissionResult Success(string transmissionId) => new(true, transmissionId, null);

    public static SdiSubmissionResult Failure(string error) => new(false, null, error);

    /// <summary>A refusal with the code of why and a short note.</summary>
    public static SdiSubmissionResult Failure(string error, string? detail) => new(false, null, error, detail);
}

/// <summary>
/// The stable codes a provider adapter answers a refused submission of a fiscal document with (E17/F01/US02/T01): short, because the document stores one
/// (<see cref="FiscalDocument.SdiErrorCodeMaxLength"/>), and turned into plain words by <see cref="SdiRejectionReasons"/>. None is an SDI code: a refusal here is the
/// channel's, before the SDI saw the file.
/// </summary>
public static class SdiSubmissionErrors
{
    /// <summary>The VAT number of the issuer is not enabled at the provider yet (the item <c>sdi_issuer_registration_pending</c> of the billing setup).</summary>
    public const string IssuerPending = "issuer_pending";

    /// <summary>The provider refused the file (it did not pass its checks, or its answer was a refusal): the file is not corrected here, the host reads the reason.</summary>
    public const string ProviderRefused = "provider_refused";

    /// <summary>The provider did not answer, or answered with an error of its own: nothing was submitted, the submission can be repeated.</summary>
    public const string ProviderError = "provider_error";

    /// <summary>The provider does not submit this kind of document (the SaaS invoices of the platform).</summary>
    public const string NotSupported = "not_supported";

    /// <summary>Every code, in the order above.</summary>
    public static IReadOnlyList<string> All { get; } = [IssuerPending, ProviderRefused, ProviderError, NotSupported];
}

/// <summary>
/// The file of a fiscal document submitted to the SDI through the channel (E17/F01/US02/T01): the port serves every issuer, the organization and the owners who issue.
/// </summary>
/// <param name="OrgId">The organization of the document.</param>
/// <param name="DocumentId">The fiscal document (the key of the idempotence of the submission: a second submission of the same document is the first).</param>
/// <param name="IssuerProfileId">The issuer profile the document was issued on.</param>
/// <param name="IssuerFiscalId">The fiscal id of the issuer the file is transmitted for: its VAT number (digits), else its fiscal code.</param>
/// <param name="IssuerName">The legal name of the issuer, for the provider that has to know the issuer (the registration of a VAT number).</param>
/// <param name="FileName">The name of the file for the SDI: <c>{country}{transmitter id}_{progressive}.xml</c>, as the technical specifications of the SDI give it (<see cref="SdiFileNames"/>).</param>
/// <param name="Xml">The FatturaPA XML as it was issued. Never logged.</param>
public sealed record SdiDocumentSubmission(Guid OrgId, Guid DocumentId, Guid IssuerProfileId, string IssuerFiscalId, string IssuerName, string FileName, string Xml);

/// <summary>What the SDI said about a submitted document, in the three states the channel tells apart.</summary>
public enum SdiOutcomeKind
{
    /// <summary>Accepted by the channel, no outcome from the SDI yet.</summary>
    Submitted = 1,

    /// <summary>The SDI delivered the invoice (or made it available to its recipient: the invoice is issued).</summary>
    Delivered = 2,

    /// <summary>The SDI discarded the file («scarto»): the invoice counts as not issued.</summary>
    Rejected = 3,
}

/// <summary>
/// The state of a submitted document as the provider reports it (<see cref="ISdiEInvoiceProvider.GetStatusAsync"/>).
/// </summary>
/// <param name="Status">One of <see cref="SdiOutcomeKind"/>; <see cref="SdiOutcomeKind.Submitted"/> while the SDI has not answered.</param>
/// <param name="ErrorCode">The code the SDI gave a rejection (the 5-digit code of its notification), when rejected.</param>
/// <param name="ErrorDetail">The text the SDI gave a rejection, when rejected: no personal data is kept (<see cref="SdiRejectionReasons.Clean"/>).</param>
public sealed record SdiStatusResult(SdiOutcomeKind Status, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>One notification of the provider about a submitted document (its webhook, <see cref="ISdiEInvoiceProvider.ReadNotificationAsync"/>).</summary>
/// <param name="TransmissionId">The provider's id of the submission it is about (<see cref="FiscalDocument.SdiTransmissionId"/>).</param>
/// <param name="Outcome">What the notification says.</param>
public sealed record SdiNotification(string TransmissionId, SdiStatusResult Outcome);

/// <summary>
/// Provider of Italian electronic invoices (PL-13, decision D9: code plus runbook, configuration outside the code). It serves the CasaZen SaaS invoices
/// (<see cref="SubmitAsync"/>) and, since E17/F01/US02, every fiscal document CasaZen issues, whoever the issuer is (<see cref="SubmitDocumentAsync"/>). The default
/// implementation is not configured: nothing is ever submitted, every paid SaaS invoice is marked <see cref="PlatformInvoiceSdiStatuses.ManualRequired"/> and the health
/// check <c>einvoicing</c> reports it, while a fiscal document stays «to send» with its XML downloadable (the issuer uploads it by hand and declares it). The adapter of
/// a provider (<c>RestSdiEInvoiceProvider</c>) implements this interface; everything of the provider stays in it. Candidate providers with public API documentation are
/// listed in <c>.claude/context/regulations/fiscale.md</c> (RS-5); the steps to add an adapter are in <c>docs/runbooks/fiscal-documents.md</c>.
/// </summary>
public interface ISdiEInvoiceProvider
{
    /// <summary>False when no provider is configured: nothing is ever submitted.</summary>
    bool IsConfigured { get; }

    /// <summary>Provider name for logs and the health check (never a secret). Never shown to a user (decision D14).</summary>
    string Name { get; }

    /// <summary>
    /// Submits the e-invoice of <paramref name="invoice"/>. Called after the Stripe event is committed, never inside
    /// its transaction. Never called when <see cref="IsConfigured"/> is false.
    /// </summary>
    Task<SdiSubmissionResult> SubmitAsync(PlatformInvoice invoice, Org org, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits the FatturaPA file of a fiscal document (E17/F01/US02/T01). Accepted: the provider's id of the submission. A refusal of the provider is an answer
    /// (<see cref="SdiSubmissionResult.Failure(string)"/> with a code of <see cref="SdiSubmissionErrors"/>), never an exception; a failure of the transport is an
    /// exception the caller does not swallow. Idempotent per <see cref="SdiDocumentSubmission.DocumentId"/>. Never called when <see cref="IsConfigured"/> is false.
    /// </summary>
    Task<SdiSubmissionResult> SubmitDocumentAsync(SdiDocumentSubmission submission, CancellationToken cancellationToken = default);

    /// <summary>The state of a submission the provider accepted (the fallback of the webhook: the job <c>fiscal-sdi-status</c>). Never called when <see cref="IsConfigured"/> is false.</summary>
    Task<SdiStatusResult> GetStatusAsync(string transmissionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What a call of the provider's webhook says (E17/F01/US02/T02): the notifications of <paramref name="payload"/>, the raw body of the call, none for an event that is not
    /// about a submission. The provider's own wire format stays here. Never called when <see cref="IsConfigured"/> is false.
    /// </summary>
    Task<IReadOnlyList<SdiNotification>> ReadNotificationAsync(string payload, CancellationToken cancellationToken = default);
}
