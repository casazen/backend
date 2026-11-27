using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Casazen.Web.BackgroundJobs;

/// <summary>Cancel unaccepted test intents when their requested service time passes, even if the guest has closed the portal.</summary>
public sealed class GuestSupplierStripeExpiryJob(
    AppDbContext db, GuestSupplierCheckout checkout, IConfiguration configuration, TimeProvider clock,
    ILogger<GuestSupplierStripeExpiryJob> logger)
{
    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!GuestSupplierCheckout.Enabled(configuration)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await db.GuestSupplierOrders
            .Where(x => x.StripeSandbox && (x.State == GuestSupplierOrderState.PaymentProcessing ||
                (x.State == GuestSupplierOrderState.Authorized && x.StripeCaptureBefore <= now) ||
                (x.RequestedFor <= now && (x.State == GuestSupplierOrderState.Quoted || x.State == GuestSupplierOrderState.Authorized ||
                    x.State == GuestSupplierOrderState.CardSaved || x.State == GuestSupplierOrderState.PaymentFailed ||
                    x.State == GuestSupplierOrderState.PaymentActionRequired))))
            .OrderBy(x => x.RequestedFor).Take(100).ToListAsync(ct);
        foreach (var order in pending)
        {
            try
            {
                if (order.RequestedFor > now || order.State == GuestSupplierOrderState.PaymentProcessing)
                    await checkout.ReconcileAsync(order, ct);
                else
                    await checkout.SupplierActionAsync(order, "decline", ct);
            }
            catch (StripeException ex)
            {
                logger.LogWarning("Guest order {OrderId} expiry will retry, Stripe code {StripeCode}", order.Id, ex.StripeError?.Code);
            }
        }
    }
}
