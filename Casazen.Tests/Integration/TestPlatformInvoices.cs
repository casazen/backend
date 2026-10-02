using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Casazen.Tests.Integration;

/// <summary>Real <see cref="PlatformInvoiceService"/> for webhook tests (PL-13): no SDI provider unless one is given.</summary>
internal static class TestPlatformInvoices
{
    public static PlatformInvoiceService Create(
        AppDbContext db,
        IStripeBillingService stripeBilling,
        ISdiEInvoiceProvider? sdiProvider = null) =>
        new(db, stripeBilling, sdiProvider ?? new UnconfiguredSdiEInvoiceProvider(), NullLogger<PlatformInvoiceService>.Instance);
}
