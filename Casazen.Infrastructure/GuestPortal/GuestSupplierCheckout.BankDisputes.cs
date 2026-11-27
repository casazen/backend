using System.Text.Json;
using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace Casazen.Infrastructure.GuestPortal;

public sealed partial class GuestSupplierCheckout
{
    private sealed class BankCase
    {
        public required string Id { get; set; }
        public required string Status { get; set; }
        public int Cents { get; set; }
        public bool AfterPayoutClaim { get; set; }
        public int SupplierRecoveryCents { get; set; }
        public int HostRecoveryCents { get; set; }
        public string? SupplierReversalId { get; set; }
        public string? HostReversalId { get; set; }
    }

    private static List<BankCase> ReadCases(GuestSupplierOrder order) =>
        order.BankDisputesJson is null ? [] : JsonSerializer.Deserialize<List<BankCase>>(order.BankDisputesJson)!;

    // The signed event only identifies the object; Stripe's current state decides the outcome, including after redelivery.
    public async Task<bool> ReconcileBankCaseAsync(GuestSupplierOrder order, string disputeId, CancellationToken ct)
    {
        if (!Enabled(configuration) || !order.StripeSandbox || order.StripePaymentIntentId is null) return false;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAsync(order, ct);
            var dispute = await new DisputeService(AdjustmentClient).GetAsync(disputeId, cancellationToken: ct);
            var intent = await Intents.GetAsync(order.StripePaymentIntentId, cancellationToken: ct);
            if (dispute.Livemode || dispute.Id != disputeId || dispute.Currency != "eur" || dispute.Amount <= 0 ||
                dispute.Amount > intent.Amount || !Matches(order, intent) || dispute.PaymentIntentId != intent.Id ||
                string.IsNullOrEmpty(intent.LatestChargeId) || dispute.ChargeId != intent.LatestChargeId) return false;

            var cases = ReadCases(order);
            var item = cases.SingleOrDefault(c => c.Id == disputeId);
            if (item == null)
            {
                item = new BankCase { Id = disputeId, Status = "new" };
                cases.Add(item);
            }
            var firstLoss = item.Status != "lost" && dispute.Status == "lost";
            item.Status = dispute.Status;
            item.Cents = checked((int)dispute.Amount);
            if (firstLoss && order.StripePayoutClaimedAt.HasValue)
            {
                item.AfterPayoutClaim = true;
                var lostBeforePayout = cases.Where(c => c.Status == "lost" && !c.AfterPayoutClaim).Sum(c => (long)c.Cents);
                var available = Math.Max(0L, intent.AmountReceived - order.RefundCents - lostBeforePayout);
                var totalPostClaimLoss = cases.Where(c => c.Status == "lost" && c.AfterPayoutClaim).Sum(c => (long)c.Cents);
                // Round the cumulative target and subtract prior recoveries so partial chargebacks never over-recover.
                int Increment(int paid, Func<BankCase, int> previous) =>
                    Math.Max(0, ProportionalRecovery(paid, totalPostClaimLoss, available) - cases.Where(c => c != item).Sum(previous));
                item.SupplierRecoveryCents = Increment(order.StripeSupplierTransferCents, c => c.SupplierRecoveryCents);
                item.HostRecoveryCents = Increment(order.StripeHostTransferCents, c => c.HostRecoveryCents);
            }
            SaveCases(order, cases);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        if (order.BankDisputeStatus == "recovery_pending") await RecoverTransfersAsync(order, ct);
        return true;
    }

    internal static int ProportionalRecovery(int transferred, long disputed, long available) =>
        available <= 0 ? 0 : (int)decimal.Round(transferred * (decimal)Math.Min(disputed, available) / available,
            0, MidpointRounding.AwayFromZero);

    private static void SaveCases(GuestSupplierOrder order, List<BankCase> cases)
    {
        order.BankDisputesJson = JsonSerializer.Serialize(cases);
        order.BankLossCents = (int)Math.Min((long)order.SupplierPriceCents + order.CommissionCents - order.RefundCents,
            cases.Where(c => c.Status == "lost").Sum(c => (long)c.Cents));
        order.BankDisputeStatus = cases.Any(c => c.SupplierRecoveryCents > 0 && c.SupplierReversalId == null ||
            c.HostRecoveryCents > 0 && c.HostReversalId == null) ? "recovery_pending"
            : cases.Any(c => c.Status is not ("won" or "lost" or "warning_closed" or "prevented")) ? "open"
            : cases.Any(c => c.Status == "lost") ? "lost" : "closed";
    }

    public async Task RecoverTransfersAsync(GuestSupplierOrder order, CancellationToken ct)
    {
        if (!Enabled(configuration) || !order.StripeSandbox) return;
        // Commit each leg separately. A later failure on the host transfer cannot lose the supplier receipt.
        foreach (var supplier in new[] { true, false })
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(order, ct);
            var cases = ReadCases(order);
            var transferId = supplier ? order.StripeSupplierTransferId : order.StripeHostTransferId;
            if (string.IsNullOrEmpty(transferId)) continue;
            var reversals = new TransferReversalService(AdjustmentClient);
            foreach (var item in cases.Where(c => c.Status == "lost"))
            {
                var amount = supplier ? item.SupplierRecoveryCents : item.HostRecoveryCents;
                if (amount == 0 || (supplier ? item.SupplierReversalId : item.HostReversalId) != null) continue;
                var metadata = new Dictionary<string, string> { ["kind"] = Kind, ["orderId"] = order.Id.ToString("D"), ["dispute"] = item.Id };
                TransferReversal? found = null;
                await foreach (var reversal in reversals.ListAutoPagingAsync(transferId,
                    new TransferReversalListOptions { Limit = 100 }, cancellationToken: ct))
                    if (reversal.Metadata?.GetValueOrDefault("dispute") == item.Id &&
                        reversal.Metadata.GetValueOrDefault("orderId") == order.Id.ToString("D"))
                    {
                        found = reversal;
                        break;
                    }
                found ??= await reversals.CreateAsync(transferId, new TransferReversalCreateOptions { Amount = amount, Metadata = metadata },
                    Key(order, $"bank-reversal:{item.Id}:{transferId}"), ct);
                if (found.Amount != amount || found.TransferId != transferId || string.IsNullOrEmpty(found.Id) ||
                    found.Metadata?.GetValueOrDefault("dispute") != item.Id)
                    throw new InvalidOperationException("Stripe transfer reversal did not match the recorded recovery.");
                if (supplier) item.SupplierReversalId = found.Id;
                else item.HostReversalId = found.Id;
            }
            SaveCases(order, cases);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
