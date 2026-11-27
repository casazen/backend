using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Default <see cref="ISdiEInvoiceProvider"/> (PL-13, A1-08, E17/F01/US02/T01): no SDI provider is configured, so nothing is
/// ever submitted, every paid platform invoice is marked
/// <see cref="PlatformInvoiceSdiStatuses.ManualRequired"/> ("fattura elettronica da emettere manualmente") and a fiscal document stays «to send», its
/// XML downloadable, until its issuer declares it sent by hand. It replaces the old <c>SdiEInvoiceService</c> stub, which logged "queued" and did nothing.
/// </summary>
/// <remarks>
/// A provider needs a contract, verifiable public documentation and its credentials (task constraint, decision D9): the adapter of one is
/// <c>RestSdiEInvoiceProvider</c>, chosen only when <c>Sdi:Provider</c> is configured. The candidates and their documentation are in
/// <c>.claude/context/regulations/fiscale.md</c> (RS-5); the steps to add an adapter are in <c>docs/runbooks/fiscal-documents.md</c>. Every method of the port
/// refuses to run: none is ever called while <see cref="IsConfigured"/> is false.
/// </remarks>
public sealed class UnconfiguredSdiEInvoiceProvider : ISdiEInvoiceProvider
{
    private const string Message = "No SDI e-invoicing provider is configured: the e-invoice must be issued manually.";

    public bool IsConfigured => false;

    public string Name => "none";

    public Task<SdiSubmissionResult> SubmitAsync(PlatformInvoice invoice, Org org, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<SdiSubmissionResult> SubmitDocumentAsync(SdiDocumentSubmission submission, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<SdiStatusResult> GetStatusAsync(string transmissionId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public Task<IReadOnlyList<SdiNotification>> ReadNotificationAsync(string payload, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);
}
