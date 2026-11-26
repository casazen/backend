using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Casazen.Infrastructure.GuestPortal;

public static class GuestSupplierRefunds
{
    public static bool BlocksPayout(GuestSupplierOrder order) =>
        order.RefundStatus is "processing" or "retrying" or "failed" or "review" ||
        order.BankDisputeStatus is "open" or "recovery_pending";
}

public sealed partial class GuestSupplierCheckout
{
    private IStripeClient AdjustmentClient => client ??= new StripeClient(configuration[$"{Section}:SecretKey"]);
    private RefundService Refunds => new(AdjustmentClient);

    public async Task<bool> DecideRefundAsync(GuestSupplierOrder order, int cents, string text, bool retry, CancellationToken ct)
    {
        if (!Enabled(configuration) || !order.StripeSandbox || string.IsNullOrWhiteSpace(text)) return false;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAsync(order, ct);
            var same = order.RefundRequestedCents == cents && order.Decision == text.Trim();
            if (order.State is (GuestSupplierOrderState.Resolved or GuestSupplierOrderState.Released) && same) return true;
            var reissue = retry && same && order.RefundStatus == "failed" && order.StripeRefundId != null;
            if (order.State != GuestSupplierOrderState.Disputed ||
                (order.StripePayoutClaimedAt.HasValue && !reissue && !(same && order.RefundStatus == "retrying")) ||
                order.BankDisputeStatus is "open" or "recovery_pending" or "lost" ||
                cents < 0 || cents > order.SupplierPriceCents + order.CommissionCents) return false;

            if (order.RefundStatus is null || reissue)
            {
                order.Decision = text.Trim();
                order.RefundRequestedCents = cents;
                if (cents == 0)
                {
                    order.State = GuestSupplierOrderState.Resolved;
                    order.RefundStatus = "not_requested";
                }
                else
                {
                    if (order.StripePaymentIntentId is null) return false;
                    order.RefundStatus = reissue ? "retrying" : "processing";
                    order.StripeRefundAttempt = Guid.NewGuid();
                    order.StripeRefundAttemptedAt = Now;
                    order.StripeRefundId = null;
                }
                await db.SaveChangesAsync(ct);
            }
            else if (!same) return false;
            await tx.CommitAsync(ct);
        }
        if (order.RefundStatus is "processing" or "retrying") await ReconcileRefundAsync(order, ct);
        return true;
    }

    public async Task ReconcileRefundAsync(GuestSupplierOrder order, CancellationToken ct)
    {
        if (!Enabled(configuration) || !order.StripeSandbox) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(order, ct);
        if (order.StripePaymentIntentId is null || order.StripeRefundAttempt is not { } attempt ||
            order.RefundStatus is not ("processing" or "retrying" or "succeeded" or "failed")) return;

        var intent = await Intents.GetAsync(order.StripePaymentIntentId, cancellationToken: ct);
        if (!Matches(order, intent) || intent.Status != "succeeded" ||
            intent.AmountReceived != order.SupplierPriceCents + order.CommissionCents) return;

        var marker = attempt.ToString("N");
        Refund? matching = null;
        var otherRefund = false;
        await foreach (var refund in Refunds.ListAutoPagingAsync(new RefundListOptions { PaymentIntent = intent.Id, Limit = 100 }, cancellationToken: ct))
        {
            if (refund.PaymentIntentId != intent.Id || refund.Currency != "eur") continue;
            if (refund.Metadata?.GetValueOrDefault("guestRefund") == marker) matching = refund;
            else if (refund.Status is not ("failed" or "canceled")) otherRefund = true;
        }
        if (matching is null && order.RefundStatus is ("processing" or "retrying") && !otherRefund && order.BankDisputeStatus is not ("open" or "lost" or "recovery_pending") &&
            (!order.StripePayoutClaimedAt.HasValue || order.RefundStatus == "retrying") && order.StripeRefundAttemptedAt > Now.AddHours(-23))
        {
            matching = await Refunds.CreateAsync(new RefundCreateOptions
            {
                PaymentIntent = intent.Id,
                Amount = order.RefundRequestedCents,
                Metadata = new() { ["kind"] = Kind, ["orderId"] = order.Id.ToString("D"), ["guestRefund"] = marker }
            }, Key(order, $"refund:{marker}"), ct);
        }
        if (matching is not null && matching.PaymentIntentId == intent.Id && matching.Currency == "eur" &&
            matching.Amount == order.RefundRequestedCents && matching.Metadata?.GetValueOrDefault("guestRefund") == marker &&
            !string.IsNullOrWhiteSpace(matching.Id))
        {
            order.StripeRefundId = matching.Id;
            order.RefundStatus = matching.Status switch
            {
                "succeeded" => "succeeded",
                "failed" or "canceled" => "failed",
                _ => order.RefundStatus == "retrying" ? "retrying" : "processing"
            };
            if (order.RefundStatus == "failed" && order.RefundCents == order.RefundRequestedCents)
            {
                order.RefundCents = 0;
                order.State = GuestSupplierOrderState.Disputed;
                if (order.StripePayoutClaimedAt == null) order.StripeReleasedAt = null;
            }
            if (order.RefundStatus == "succeeded")
            {
                order.RefundCents = order.RefundRequestedCents;
                order.State = order.StripeReleasedAt != null && order.StripePayoutClaimedAt != null
                    ? GuestSupplierOrderState.Released : GuestSupplierOrderState.Resolved;
            }
        }
        else if (otherRefund || order.StripeRefundAttemptedAt <= Now.AddHours(-23))
            order.RefundStatus = "review";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
