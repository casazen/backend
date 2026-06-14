using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class SdiEInvoiceService(ILogger<SdiEInvoiceService> logger) : ISdiEInvoiceService
{
    public Task<string?> TransmitInvoiceAsync(PlatformInvoice invoice, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "SDI stub: invoice {InvoiceId} for org {OrgId} queued (no-op in dev)",
            invoice.StripeInvoiceId,
            invoice.OrgId);
        return Task.FromResult<string?>(null);
    }
}
