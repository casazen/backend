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

/// <summary>Outcome of <see cref="ISdiEInvoiceProvider.SubmitAsync"/>.</summary>
/// <param name="Accepted">True only when the provider accepted the invoice and returned its id.</param>
/// <param name="TransmissionId">The provider's id of the submission, when accepted.</param>
/// <param name="Error">Why the submission failed (no PII), when not accepted.</param>
public sealed record SdiSubmissionResult(bool Accepted, string? TransmissionId, string? Error)
{
    public static SdiSubmissionResult Success(string transmissionId) => new(true, transmissionId, null);

    public static SdiSubmissionResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Provider of Italian electronic invoices for the CasaZen SaaS invoices (PL-13, decision D9: code plus runbook,
/// configuration outside the code). This build ships no commercial adapter: the default implementation is not
/// configured, every paid invoice is marked <see cref="PlatformInvoiceSdiStatuses.ManualRequired"/> and the health
/// check <c>einvoicing</c> reports it. Candidate providers with public API documentation are listed in
/// <c>.claude/context/regulations/fiscale.md</c> (RS-5); an adapter implements this interface.
/// </summary>
public interface ISdiEInvoiceProvider
{
    /// <summary>False when no provider is configured: nothing is ever submitted.</summary>
    bool IsConfigured { get; }

    /// <summary>Provider name for logs and the health check (never a secret).</summary>
    string Name { get; }

    /// <summary>
    /// Submits the e-invoice of <paramref name="invoice"/>. Called after the Stripe event is committed, never inside
    /// its transaction. Never called when <see cref="IsConfigured"/> is false.
    /// </summary>
    Task<SdiSubmissionResult> SubmitAsync(PlatformInvoice invoice, Org org, CancellationToken cancellationToken = default);
}
