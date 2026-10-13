using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Default <see cref="ISdiEInvoiceProvider"/> (PL-13, A1-08): no SDI provider is integrated in this build, so nothing is
/// ever submitted and every paid platform invoice is marked
/// <see cref="PlatformInvoiceSdiStatuses.ManualRequired"/> ("fattura elettronica da emettere manualmente"). It replaces
/// the old <c>SdiEInvoiceService</c> stub, which logged "queued" and did nothing.
/// </summary>
/// <remarks>
/// No commercial provider is wired without verifiable public documentation and a contract (task constraint, decision
/// D9). The candidates and their documentation are in <c>.claude/context/regulations/fiscale.md</c> (RS-5); the steps
/// to add an adapter are in <c>docs/runbooks/billing-tax.md</c>.
/// </remarks>
public sealed class UnconfiguredSdiEInvoiceProvider : ISdiEInvoiceProvider
{
    public bool IsConfigured => false;

    public string Name => "none";

    public Task<SdiSubmissionResult> SubmitAsync(PlatformInvoice invoice, Org org, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No SDI e-invoicing provider is configured: the e-invoice must be issued manually.");
}
