using Casazen.Infrastructure.GuestPortal;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

public sealed class GuestSupplierPayoutJob(GuestSupplierPayouts payouts, IConfiguration configuration)
{
    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!GuestSupplierPayouts.Enabled(configuration)) return;
        foreach (var id in await payouts.DueOrders().Take(100).ToListAsync(ct))
            await payouts.ReleaseAsync(id, ct);
    }
}
