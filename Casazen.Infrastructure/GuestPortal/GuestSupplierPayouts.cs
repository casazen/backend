using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stripe;

namespace Casazen.Infrastructure.GuestPortal;

/// <summary>Release captured sandbox funds to connected balances after the service dispute window.</summary>
public sealed class GuestSupplierPayouts(AppDbContext db, IConfiguration configuration, TimeProvider clock)
{
    private IStripeClient? client;
    private IStripeClient Client => client ??= new StripeClient(configuration[$"{GuestSupplierCheckout.Section}:SecretKey"]);
    internal GuestSupplierPayouts(AppDbContext db, IConfiguration config, TimeProvider clock, IStripeClient client)
        : this(db, config, clock) => this.client = client;

    public static bool Enabled(IConfiguration config) => GuestSupplierCheckout.Enabled(config) &&
        config.GetValue<bool>($"{GuestSupplierCheckout.Section}:PayoutsEnabled");

    internal sealed record Shares(int Supplier, int Host, int Platform);

    internal static Shares Split(GuestSupplierOrder order, int cardFeeCents, int chargebackCents = 0)
    {
        var total = checked(order.SupplierPriceCents + order.CommissionCents);
        var returned = checked(order.RefundCents + chargebackCents);
        if (total <= 0 || returned < 0 || returned > total || cardFeeCents < 0)
            throw new ArgumentOutOfRangeException(nameof(order));
        var commissionRefund = (int)Math.Round(returned * (decimal)order.CommissionCents / total, MidpointRounding.AwayFromZero);
        var netCommission = order.CommissionCents - commissionRefund;
        // D34 assigns the actual card processing cost to the supplier for both private and marketplace orders.
        var supplier = order.SupplierPriceCents - (returned - commissionRefund) - cardFeeCents;
        return new Shares(supplier, order.HostOrigin ? netCommission : 0, order.HostOrigin ? 0 : netCommission);
    }

    public IQueryable<Guid> DueOrders()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now - GuestSupplierOrders.DisputeWindow;
        return db.GuestSupplierOrders.AsNoTracking()
            .Where(o => o.StripeSandbox && o.StripePaymentIntentId != null && o.StripeReleasedAt == null &&
                (o.StripePayoutAttemptAfter == null || o.StripePayoutAttemptAfter <= now) &&
                (o.StripePayoutClaimedAt != null || o.CompletedAt <= cutoff &&
                    (o.State == GuestSupplierOrderState.Completed || o.State == GuestSupplierOrderState.Resolved) &&
                    o.RefundCents < o.SupplierPriceCents + o.CommissionCents))
            .OrderBy(o => o.StripePayoutAttemptAfter ?? o.CompletedAt).ThenBy(o => o.Id).Select(o => o.Id);
    }

    public async Task<bool> ReleaseAsync(Guid id, CancellationToken ct)
    {
        if (!Enabled(configuration)) return false;
        // Commit the plan before any Stripe write, then each transfer receipt separately.
        for (var step = 0; step < 4; step++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var order = await db.GuestSupplierOrders.FromSqlInterpolated(
                $"SELECT *, xmin FROM \"GuestSupplierOrders\" WHERE \"Id\" = {id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
            if (order is null || !order.StripeSandbox || order.StripePaymentIntentId is null) return false;
            if (order.StripeReleasedAt.HasValue) return true;
            var now = clock.GetUtcNow().UtcDateTime;
            if (order.CompletedAt is not { } completed || now < completed + GuestSupplierOrders.DisputeWindow) return false;
            var tracked = db.GuestSupplierOrders.Local.FirstOrDefault(o => o.Id == id);
            if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
            db.Attach(order);
            string? failure;
            try { failure = await AdvanceAsync(order, now, ct); }
            catch (StripeException ex)
            {
                // A timeout after creation is safe: the next attempt recovers the receipt from the order's transfer group.
                failure = ex.StripeError?.Code ?? "stripe_temporarily_unavailable";
            }
            order.StripePayoutFailureCode = failure;
            order.StripePayoutAttemptAfter = failure == null ? null : now.AddMinutes(10);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (failure != null) return false;
            if (order.StripeReleasedAt.HasValue) return true;
        }
        return false;
    }

    private async Task<string?> AdvanceAsync(GuestSupplierOrder order, DateTime now, CancellationToken ct)
    {
        if (order.StripePayoutClaimedAt.HasValue)
        {
            var recoveryError = await RecoverAsync(order, ct);
            if (recoveryError != null) return recoveryError;
        }
        if (order.State is not (GuestSupplierOrderState.Completed or GuestSupplierOrderState.Resolved))
            return "order_disputed";
        var intent = await new PaymentIntentService(Client).GetAsync(order.StripePaymentIntentId!,
            new PaymentIntentGetOptions { Expand = ["latest_charge.balance_transaction"] }, cancellationToken: ct);
        if (!GuestSupplierCheckout.Matches(order, intent) || intent.Status != "succeeded" || intent.AmountReceived != intent.Amount ||
            intent.LatestCharge is not { Livemode: false, Captured: true, Paid: true, Currency: "eur" } charge ||
            charge.PaymentIntentId != intent.Id || charge.AmountCaptured != intent.Amount)
            return "charge_unverified";
        if (charge.BalanceTransaction is not { Currency: "eur" } balance || balance.Amount != charge.AmountCaptured ||
            balance.Fee < 0 || balance.Fee > int.MaxValue ||
            balance.Fee == 0 && balance.FeeDetails is not { Count: > 0 }) return "fee_unavailable";
        if (charge.Disputed)
        {
            var disputes = await new DisputeService(Client).ListAsync(new DisputeListOptions { Charge = charge.Id, Limit = 100 }, cancellationToken: ct);
            if (disputes.HasMore || disputes.Data.Count == 0 || disputes.Data.Any(d => d.Status is not ("won" or "warning_closed")))
                return "bank_dispute";
        }
        var refunds = await new RefundService(Client).ListAsync(new RefundListOptions { Charge = charge.Id, Limit = 100 }, cancellationToken: ct);
        if (refunds.HasMore || refunds.Data.Any(r => r.Status is not ("succeeded" or "canceled" or "failed")) ||
            refunds.Data.Where(r => r.Status == "succeeded").Sum(r => r.Amount) != order.RefundCents ||
            charge.AmountRefunded != order.RefundCents) return "refund_unreconciled";

        var share = Split(order, (int)balance.Fee);
        if (share.Supplier < 0 || (share.Supplier == 0 && share.Host == 0))
            return "no_distributable_supplier_balance";
        if (!order.StripePayoutClaimedAt.HasValue)
        {
            var supplier = share.Supplier > 0 ? await DestinationAsync(order.SupplierOrgId, ct) : null;
            var host = share.Host > 0 ? await DestinationAsync(order.HostOrgId, ct) : null;
            if (share.Supplier > 0 && supplier is null || share.Host > 0 && host is null) return "account_not_ready";
            order.StripePayoutClaimedAt = now;
            order.StripePayoutChargeId = charge.Id;
            order.StripeCardFeeCents = (int)balance.Fee;
            order.StripeSupplierDestination = supplier;
            order.StripeHostDestination = host;
            order.StripeSupplierTransferCents = share.Supplier;
            order.StripeHostTransferCents = share.Host;
            return null;
        }
        if (order.StripePayoutChargeId != charge.Id || order.StripeCardFeeCents != balance.Fee ||
            order.StripeSupplierTransferCents != share.Supplier || order.StripeHostTransferCents != share.Host)
            return "payout_plan_changed";

        if (share.Supplier > 0 && order.StripeSupplierTransferId == null)
            return await CreateTransferAsync(order, "supplier", ct);
        if (share.Host > 0 && order.StripeHostTransferId == null)
            return await CreateTransferAsync(order, "host", ct);
        order.State = GuestSupplierOrderState.Released;
        order.StripeReleasedAt = now;
        return null;
    }

    private async Task<string?> DestinationAsync(Guid orgId, CancellationToken ct) =>
        await db.Orgs.Where(o => o.Id == orgId && o.ConnectPayoutsEnabled && o.ConnectChargesEnabled &&
            o.StripeConnectedAccountId != null && o.StripeConnectedAccountId.StartsWith("acct_"))
            .Select(o => o.StripeConnectedAccountId).SingleOrDefaultAsync(ct);

    internal static string Group(GuestSupplierOrder order) => $"group_{order.StripePaymentIntentId}";

    private async Task<string?> RecoverAsync(GuestSupplierOrder order, CancellationToken ct)
    {
        var transfers = await new TransferService(Client).ListAsync(
            new TransferListOptions { TransferGroup = Group(order), Limit = 100 }, cancellationToken: ct);
        if (transfers.HasMore) return "unexpected_transfers";
        foreach (var transfer in transfers.Data)
        {
            var role = transfer.Metadata?.GetValueOrDefault("role");
            if (role is not ("supplier" or "host") || !Record(order, role, transfer)) return "transfer_unverified";
            if (transfer.AmountReversed != 0) return "transfer_reversed";
        }
        return null;
    }

    private async Task<string?> CreateTransferAsync(GuestSupplierOrder order, string role, CancellationToken ct)
    {
        var supplier = role == "supplier";
        var transfer = await new TransferService(Client).CreateAsync(new TransferCreateOptions
        {
            Amount = supplier ? order.StripeSupplierTransferCents : order.StripeHostTransferCents,
            Currency = "eur",
            SourceTransaction = order.StripePayoutChargeId,
            TransferGroup = Group(order),
            Destination = supplier ? order.StripeSupplierDestination : order.StripeHostDestination,
            Metadata = new() { ["kind"] = GuestSupplierCheckout.Kind, ["orderId"] = order.Id.ToString("D"), ["role"] = role }
        }, new RequestOptions { IdempotencyKey = $"{Group(order)}:{role}" }, ct);
        return Record(order, role, transfer) && transfer.AmountReversed == 0 ? null : "transfer_unverified";
    }

    private static bool Record(GuestSupplierOrder order, string role, Transfer transfer)
    {
        var supplier = role == "supplier";
        var known = supplier ? order.StripeSupplierTransferId : order.StripeHostTransferId;
        if (transfer.Livemode || string.IsNullOrEmpty(transfer.Id) || transfer.Currency != "eur" ||
            transfer.SourceTransactionId != order.StripePayoutChargeId || transfer.TransferGroup != Group(order) ||
            transfer.Metadata?.GetValueOrDefault("orderId") != order.Id.ToString("D") ||
            transfer.Metadata.GetValueOrDefault("kind") != GuestSupplierCheckout.Kind || transfer.Metadata.GetValueOrDefault("role") != role ||
            transfer.DestinationId != (supplier ? order.StripeSupplierDestination : order.StripeHostDestination) ||
            transfer.Amount != (supplier ? order.StripeSupplierTransferCents : order.StripeHostTransferCents) ||
            known != null && known != transfer.Id) return false;
        if (supplier) order.StripeSupplierTransferId = transfer.Id;
        else order.StripeHostTransferId = transfer.Id;
        return true;
    }
}
