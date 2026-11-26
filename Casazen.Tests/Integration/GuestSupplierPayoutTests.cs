using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed class GuestSupplierPayoutTests
{
    [Theory]
    [InlineData(true, 0, 960, 100, 0)]
    [InlineData(false, 0, 960, 0, 100)]
    [InlineData(true, 110, 860, 90, 0)]
    [InlineData(false, 110, 860, 0, 90)]
    [InlineData(false, 55, 910, 0, 95)]
    public void Split_DeductsFeeFromSupplierAndReturnsCommissionProportionally(
        bool hostOrigin, int refunded, int supplier, int host, int platform)
    {
        var order = new GuestSupplierOrder
        {
            ServiceName = "Service",
            SupplierName = "Supplier",
            HostOrigin = hostOrigin,
            SupplierPriceCents = 1000,
            CommissionCents = 100,
            RefundCents = refunded
        };
        Assert.Equal(new GuestSupplierPayouts.Shares(supplier, host, platform), GuestSupplierPayouts.Split(order, 40));
    }

    [PostgresFact]
    public async Task Release_HoldsDisputesAndMissingFees_ReconcilesLostTransferResponseAndFrozenDestinations()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("payoutretry");
        await using var db = database.CreateContext();
        var config = Config("sk_test_fake", "pk_test_fake");
        var order = await SeedAsync(db, "acct_supplier", "acct_host");
        order.State = GuestSupplierOrderState.Completed;
        order.StripePaymentIntentId = "pi_fixture";
        order.CompletedAt = DateTime.UtcNow.AddHours(-23);
        await db.SaveChangesAsync();
        var intent = new PaymentIntent
        {
            Id = "pi_fixture",
            Status = "succeeded",
            Currency = "eur",
            CaptureMethod = "manual",
            Amount = 1100,
            AmountReceived = 1100,
            Metadata = new() { ["kind"] = GuestSupplierCheckout.Kind, ["orderId"] = order.Id.ToString("D") },
            LatestCharge = new Charge
            {
                Id = "ch_fixture",
                Currency = "eur",
                AmountCaptured = 1100,
                Paid = true,
                Captured = true,
                PaymentIntentId = "pi_fixture"
            }
        };
        var transfers = new List<Transfer>();
        var client = new Mock<IStripeClient>();
        client.SetupGet(x => x.ApiBase).Returns("https://api.stripe.com");
        client.Setup(x => x.RequestAsync<PaymentIntent>(HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(intent);
        client.Setup(x => x.RequestAsync<StripeList<Refund>>(HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StripeList<Refund> { Data = [] });
        client.Setup(x => x.RequestAsync<StripeList<Dispute>>(HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new StripeList<Dispute> { Data = [new Dispute { Status = "under_review" }] });
        client.Setup(x => x.RequestAsync<StripeList<Transfer>>(HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new StripeList<Transfer> { Data = [.. transfers] });
        client.Setup(x => x.RequestAsync<Transfer>(HttpMethod.Post, It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).Returns((HttpMethod method, string path,
            BaseOptions options, RequestOptions request, CancellationToken _) =>
        {
            Assert.Equal("/v1/transfers", path);
            var create = Assert.IsType<TransferCreateOptions>(options);
            var role = create.Metadata["role"];
            Assert.Equal($"group_pi_fixture:{role}", request.IdempotencyKey);
            Assert.Null(request.StripeAccount);
            var transfer = new Transfer
            {
                Id = $"tr_{role}",
                DestinationId = create.Destination,
                Amount = create.Amount!.Value,
                Currency = create.Currency,
                SourceTransactionId = create.SourceTransaction,
                TransferGroup = create.TransferGroup,
                Metadata = create.Metadata
            };
            Assert.DoesNotContain(transfers, t => t.Id == transfer.Id);
            transfers.Add(transfer);
            if (role == "supplier") throw new StripeException("response lost after transfer creation");
            return Task.FromResult(transfer);
        });
        var payouts = new GuestSupplierPayouts(db, config, TimeProvider.System, client.Object);
        Assert.False(await payouts.ReleaseAsync(order.Id, default));
        var saved = await ReloadAsync(db, order.Id);
        saved.CompletedAt = DateTime.UtcNow.AddHours(-25);
        saved.State = GuestSupplierOrderState.Disputed;
        await db.SaveChangesAsync();
        Assert.False(await payouts.ReleaseAsync(order.Id, default));
        saved = await ReloadAsync(db, order.Id);
        saved.State = GuestSupplierOrderState.Resolved;
        await db.SaveChangesAsync();
        Assert.False(await payouts.ReleaseAsync(order.Id, default));
        Assert.Equal("fee_unavailable", (await ReloadAsync(db, order.Id)).StripePayoutFailureCode);
        intent.LatestCharge.BalanceTransaction = new BalanceTransaction { Id = "txn_fee", Currency = "eur", Fee = 40, Amount = 1100 };
        intent.LatestCharge.Disputed = true;
        Assert.False(await payouts.ReleaseAsync(order.Id, default));
        Assert.Equal("bank_dispute", (await ReloadAsync(db, order.Id)).StripePayoutFailureCode);
        intent.LatestCharge.Disputed = false;
        intent.LatestCharge.AmountRefunded = 55;
        Assert.False(await payouts.ReleaseAsync(order.Id, default));
        Assert.Equal("refund_unreconciled", (await ReloadAsync(db, order.Id)).StripePayoutFailureCode);
        intent.LatestCharge.AmountRefunded = 0;
        Assert.False(await payouts.ReleaseAsync(order.Id, default));
        Assert.Single(transfers);
        Assert.Null((await ReloadAsync(db, order.Id)).StripeSupplierTransferId);
        var host = await db.Orgs.FindAsync(order.HostOrgId);
        host!.StripeConnectedAccountId = "acct_changed";
        await db.SaveChangesAsync();
        Assert.True(await payouts.ReleaseAsync(order.Id, default));
        Assert.True(await payouts.ReleaseAsync(order.Id, default));
        saved = await ReloadAsync(db, order.Id);
        Assert.Equal(GuestSupplierOrderState.Released, saved.State);
        Assert.NotNull(saved.StripeReleasedAt);
        Assert.Equal(40, saved.StripeCardFeeCents);
        Assert.Collection(transfers,
            t => { Assert.Equal(960, t.Amount); Assert.Equal("acct_supplier", t.DestinationId); },
            t => { Assert.Equal(100, t.Amount); Assert.Equal("acct_host", t.DestinationId); });
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ConnectSandboxTheoryAttribute : TheoryAttribute
    {
        public ConnectSandboxTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY")?.StartsWith("sk_test_") != true ||
                Environment.GetEnvironmentVariable("STRIPE_TEST_PUBLISHABLE_KEY")?.StartsWith("pk_test_") != true ||
                Environment.GetEnvironmentVariable("STRIPE_TEST_SUPPLIER_ACCOUNT")?.StartsWith("acct_") != true ||
                Environment.GetEnvironmentVariable("STRIPE_TEST_HOST_ACCOUNT")?.StartsWith("acct_") != true)
                Skip = "Requires opt-in Stripe sandbox keys and two synthetic Connect accounts.";
        }
    }

    [ConnectSandboxTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StripeSandbox_SplitsRealCharge(bool hostOrigin)
    {
        var secret = Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY")!;
        var supplier = Environment.GetEnvironmentVariable("STRIPE_TEST_SUPPLIER_ACCOUNT")!;
        var host = Environment.GetEnvironmentVariable("STRIPE_TEST_HOST_ACCOUNT")!;
        Assert.True(supplier != host);
        var config = Config(secret, Environment.GetEnvironmentVariable("STRIPE_TEST_PUBLISHABLE_KEY")!);
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("payoutstripe");
        await using var db = database.CreateContext();
        var order = await SeedAsync(db, supplier, host);
        order.HostOrigin = hostOrigin;
        if (!hostOrigin)
        {
            var org = await db.Orgs.FindAsync(order.HostOrgId);
            org!.StripeConnectedAccountId = null;
            org.ConnectPayoutsEnabled = false;
        }
        await db.SaveChangesAsync();
        var client = new StripeClient(secret);
        var intents = new PaymentIntentService(client);
        var checkout = new GuestSupplierCheckout(db, TimeProvider.System, config);
        Assert.NotNull(await checkout.StartAsync(order, 1100, default));
        var id = order.StripePaymentIntentId!;
        var transfers = new TransferService(client);
        var group = GuestSupplierPayouts.Group(order);
        try
        {
            var hold = await intents.ConfirmAsync(id, new PaymentIntentConfirmOptions { PaymentMethod = "pm_card_visa" });
            Assert.False(hold.Livemode);
            Assert.Equal("requires_capture", hold.Status);
            Assert.True(await checkout.SupplierActionAsync(order, "accept", default));
            order.State = GuestSupplierOrderState.Completed;
            order.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var payouts = new GuestSupplierPayouts(db, config, TimeProvider.System);
            Assert.False(await payouts.ReleaseAsync(order.Id, default));
            order = await ReloadAsync(db, order.Id);
            order.CompletedAt = DateTime.UtcNow.AddHours(-25);
            await db.SaveChangesAsync();
            for (var attempt = 0; attempt < 15 && !await payouts.ReleaseAsync(order.Id, default); attempt++)
                await Task.Delay(TimeSpan.FromSeconds(2));
            order = await ReloadAsync(db, order.Id);
            Assert.True(order.State == GuestSupplierOrderState.Released, $"Payout result: {order.StripePayoutFailureCode}");
            Assert.True(await payouts.ReleaseAsync(order.Id, default));
            var intent = await intents.GetAsync(id, new PaymentIntentGetOptions { Expand = ["latest_charge.balance_transaction"] });
            Assert.True(intent.LatestCharge.BalanceTransaction.Fee > 0);
            Assert.Equal(intent.LatestCharge.BalanceTransaction.Fee, (long)order.StripeCardFeeCents!.Value);
            var list = await transfers.ListAsync(new TransferListOptions { TransferGroup = group });
            Assert.Equal(hostOrigin ? 2 : 1, list.Data.Count);
            Assert.All(list.Data, transfer => { Assert.False(transfer.Livemode); Assert.Equal(intent.LatestChargeId, transfer.SourceTransactionId); });
            Assert.Equal(1000 - intent.LatestCharge.BalanceTransaction.Fee, list.Data.Single(t => t.DestinationId == supplier).Amount);
            if (hostOrigin)
                Assert.Equal(100, list.Data.Single(t => t.DestinationId == host).Amount);
            else
                Assert.DoesNotContain(list.Data, t => t.DestinationId == host);
        }
        finally
        {
            var intent = await intents.GetAsync(id);
            if (!intent.Livemode)
            {
                var list = await transfers.ListAsync(new TransferListOptions { TransferGroup = group });
                foreach (var t in list.Data.Where(t => !t.Livemode && t.SourceTransactionId == intent.LatestChargeId &&
                    t.Metadata.GetValueOrDefault("orderId") == order.Id.ToString("D") && t.AmountReversed < t.Amount))
                    await new TransferReversalService(client).CreateAsync(t.Id, new TransferReversalCreateOptions());
                if (intent.Status == "succeeded")
                    await new RefundService(client).CreateAsync(new RefundCreateOptions { PaymentIntent = id });
                else if (intent.Status != "canceled") await intents.CancelAsync(id);
            }
        }
    }

    private static async Task<GuestSupplierOrder> SeedAsync(AppDbContext db, string supplierAccount, string hostAccount)
    {
        var order = await GuestSupplierStripeSandboxTests.SeedOrderAsync(db, DateTime.UtcNow);
        order.HostOrigin = true;
        order.CommissionBasisPoints = 1000;
        order.SupplierPriceCents = 1000;
        order.CommissionCents = 100;
        foreach (var org in await db.Orgs.Where(o => o.Id == order.SupplierOrgId || o.Id == order.HostOrgId).ToListAsync())
        {
            org.StripeConnectedAccountId = org.Id == order.HostOrgId ? hostAccount : supplierAccount;
            org.ConnectChargesEnabled = true;
            org.ConnectPayoutsEnabled = true;
        }
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<GuestSupplierOrder> ReloadAsync(AppDbContext db, Guid orderId)
    {
        db.ChangeTracker.Clear();
        return await db.GuestSupplierOrders.SingleAsync(o => o.Id == orderId);
    }

    private static IConfiguration Config(string secret, string publishable)
    {
        var config = GuestSupplierStripeSandboxTests.Config(secret, publishable, "whsec_synthetic");
        config[$"{GuestSupplierCheckout.Section}:PayoutsEnabled"] = "true";
        return config;
    }
}
