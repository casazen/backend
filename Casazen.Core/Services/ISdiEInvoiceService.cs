using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ISdiEInvoiceService
{
    Task<string?> TransmitInvoiceAsync(PlatformInvoice invoice, CancellationToken cancellationToken = default);
}
