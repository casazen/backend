using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Casazen.Web.BackgroundJobs;

public sealed class GuestSupplierAdjustmentJob(AppDbContext db, GuestSupplierCheckout checkout,
    IConfiguration configuration, ILogger<GuestSupplierAdjustmentJob> logger)
{
    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!GuestSupplierCheckout.Enabled(configuration)) return;
        Guid? after = null;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.GuestSupplierOrders.Where(x => x.StripeSandbox &&
                    (x.RefundStatus == "processing" || x.BankDisputeStatus == "recovery_pending") &&
                    (after == null || x.Id.CompareTo(after.Value) > 0))
                .OrderBy(x => x.Id).Take(50).ToListAsync(ct);
            if (batch.Count == 0) return;
            foreach (var order in batch)
            {
                try
                {
                    if (order.BankDisputeStatus == "recovery_pending") await checkout.RecoverTransfersAsync(order, ct);
                    if (order.RefundStatus == "processing") await checkout.ReconcileRefundAsync(order, ct);
                }
                catch (StripeException ex)
                {
                    logger.LogWarning("Guest order {OrderId} adjustment awaits Stripe reconciliation ({StripeCode})", order.Id, ex.StripeError?.Code);
                }
            }
            after = batch[^1].Id;
            db.ChangeTracker.Clear();
        }
    }
}
