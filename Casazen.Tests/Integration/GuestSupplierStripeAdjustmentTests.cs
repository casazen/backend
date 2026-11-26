using Casazen.Core.Entities;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Integration.Postgres;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed partial class GuestSupplierStripeSandboxTests
{
    [StripeSandboxFact]
    public async Task RealSandbox_CommercialRefund_FullAndPartialAreConfirmedOnce()
    {
        var secret = Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY")!;
        var config = Config(secret, Environment.GetEnvironmentVariable("STRIPE_TEST_PUBLISHABLE_KEY")!, "whsec_test");
        var stripe = new StripeClient(secret);
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("guestrefundlive");
        foreach (var cents in new[] { 55, 110 })
        {
            await using var db = database.CreateContext();
            var order = await SeedOrderAsync(db, DateTime.UtcNow);
            var checkout = new GuestSupplierCheckout(db, TimeProvider.System, config);
            var intent = await CreateCapturedAsync(stripe, order, "pm_card_visa");
            order.StripePaymentIntentId = intent.Id;
            order.State = GuestSupplierOrderState.Disputed;
            await db.SaveChangesAsync();
            Assert.True(await checkout.DecideRefundAsync(order, cents, "Service adjustment", false, default));
            Assert.True(await checkout.DecideRefundAsync(order, cents, "Service adjustment", false, default));
            await db.Entry(order).ReloadAsync();
            Assert.Equal("succeeded", order.RefundStatus);
            Assert.Equal(cents, order.RefundCents);
            var providerRefund = Assert.Single((await new RefundService(stripe).ListAsync(new RefundListOptions { PaymentIntent = intent.Id })).Data);
            Assert.Equal(order.StripeRefundId, providerRefund.Id);
            Assert.Equal(cents, providerRefund.Amount);
            Assert.Equal(GuestSupplierOrderState.Resolved, order.State);
        }
    }

    [StripeSandboxFact]
    public async Task RealSandbox_BankLoss_ReversesSupplierAndHostExactlyOnce()
    {
        var secret = Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY")!;
        var supplier = Environment.GetEnvironmentVariable("STRIPE_TEST_SUPPLIER_ACCOUNT");
        var host = Environment.GetEnvironmentVariable("STRIPE_TEST_HOST_ACCOUNT");
        Assert.False(string.IsNullOrWhiteSpace(supplier) || string.IsNullOrWhiteSpace(host) || supplier == host,
            "Two different test Connect recipients are required for this scenario.");
        var stripe = new StripeClient(secret);
        var config = Config(secret, Environment.GetEnvironmentVariable("STRIPE_TEST_PUBLISHABLE_KEY")!, "whsec_test");
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("guestbanklive");
        await using var db = database.CreateContext();
        var order = await SeedOrderAsync(db, DateTime.UtcNow);
        var checkout = new GuestSupplierCheckout(db, TimeProvider.System, config);
        var intent = await CreateCapturedAsync(stripe, order, "pm_card_createDispute");
        var transfers = new TransferService(stripe);
        var supplierTransfer = await transfers.CreateAsync(new TransferCreateOptions { Amount = 70, Currency = "eur", Destination = supplier, TransferGroup = order.Id.ToString("N") });
        var hostTransfer = await transfers.CreateAsync(new TransferCreateOptions { Amount = 10, Currency = "eur", Destination = host, TransferGroup = order.Id.ToString("N") });
        order.StripePaymentIntentId = intent.Id;
        order.State = GuestSupplierOrderState.Released;
        order.StripePayoutClaimedAt = DateTime.UtcNow;
        order.StripeReleasedAt = DateTime.UtcNow;
        order.StripeSupplierTransferCents = 70;
        order.StripeSupplierTransferId = supplierTransfer.Id;
        order.StripeHostTransferCents = 10;
        order.StripeHostTransferId = hostTransfer.Id;
        await db.SaveChangesAsync();
        var disputes = new DisputeService(stripe);
        Dispute? dispute = null;
        for (var attempt = 0; attempt < 60 && dispute == null; attempt++)
        {
            dispute = (await disputes.ListAsync(new DisputeListOptions { PaymentIntent = intent.Id })).Data.SingleOrDefault();
            if (dispute == null) await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.NotNull(dispute);
        Assert.True(await checkout.ReconcileBankCaseAsync(order, dispute.Id, default));
        Assert.Equal("open", order.BankDisputeStatus);
        Assert.Equal(0, (await transfers.GetAsync(supplierTransfer.Id)).AmountReversed);
        await disputes.CloseAsync(dispute.Id);
        for (var attempt = 0; attempt < 30 && (await disputes.GetAsync(dispute.Id)).Status != "lost"; attempt++)
            await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(await checkout.ReconcileBankCaseAsync(order, dispute.Id, default));
        Assert.True(await checkout.ReconcileBankCaseAsync(order, dispute.Id, default));
        Assert.Equal("lost", order.BankDisputeStatus);
        Assert.Equal(110, order.BankLossCents);
        var reversals = new TransferReversalService(stripe);
        Assert.Equal(70, Assert.Single((await reversals.ListAsync(supplierTransfer.Id)).Data).Amount);
        Assert.Equal(10, Assert.Single((await reversals.ListAsync(hostTransfer.Id)).Data).Amount);
    }

    private static async Task<PaymentIntent> CreateCapturedAsync(IStripeClient stripe, GuestSupplierOrder order, string testCard)
    {
        var intents = new PaymentIntentService(stripe);
        var intent = await intents.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = order.SupplierPriceCents + order.CommissionCents,
            Currency = "eur",
            PaymentMethodTypes = ["card"],
            PaymentMethod = testCard,
            CaptureMethod = "manual",
            Confirm = true,
            Metadata = new() { ["kind"] = GuestSupplierCheckout.Kind, ["orderId"] = order.Id.ToString("D") }
        });
        Assert.False(intent.Livemode);
        return await intents.CaptureAsync(intent.Id);
    }

    [PostgresFact]
    public async Task CommercialRefund_LostResponseAndCallbacksReconcileOneOperation()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("guestrefundreplay");
        await using var db = database.CreateContext();
        var order = await SeedOrderAsync(db, DateTime.UtcNow);
        order.StripePaymentIntentId = "pi_test";
        order.State = GuestSupplierOrderState.Disputed;
        await db.SaveChangesAsync();
        var client = new Mock<IStripeClient>();
        client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        client.Setup(c => c.RequestAsync<PaymentIntent>(HttpMethod.Get, It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PaymentIntent
            {
                Id = "pi_test",
                Amount = 110,
                AmountReceived = 110,
                Currency = "eur",
                Status = "succeeded",
                CaptureMethod = "manual",
                Metadata = new() { ["kind"] = GuestSupplierCheckout.Kind, ["orderId"] = order.Id.ToString("D") }
            });
        List<Refund> rows = [];
        client.Setup(c => c.RequestAsync<StripeList<Refund>>(HttpMethod.Get, "/v1/refunds", It.IsAny<BaseOptions>(),
                It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new StripeList<Refund> { Data = rows });
        client.Setup(c => c.RequestAsync<Refund>(HttpMethod.Post, "/v1/refunds", It.IsAny<BaseOptions>(),
                It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod _, string _, BaseOptions options, RequestOptions request, CancellationToken _) =>
            {
                var refund = Assert.IsType<RefundCreateOptions>(options);
                Assert.Equal("pi_test", refund.PaymentIntent);
                Assert.Contains(refund.Metadata["guestRefund"], request.IdempotencyKey);
                rows.Add(new Refund { Id = "re_test", PaymentIntentId = "pi_test", Amount = 55, Currency = "eur", Metadata = refund.Metadata, Status = "pending" });
                return Task.FromException<Refund>(new HttpRequestException("Response lost"));
            });
        var checkout = new GuestSupplierCheckout(db, TimeProvider.System, Config("sk_test_fixture", "pk_test_fixture", "whsec_fixture"), client.Object);
        await Assert.ThrowsAsync<HttpRequestException>(() => checkout.DecideRefundAsync(order, 55, "Partial", false, default));
        await db.Entry(order).ReloadAsync();
        Assert.Equal("processing", order.RefundStatus);
        Assert.True(GuestSupplierRefunds.BlocksPayout(order));
        Assert.Equal(0, order.RefundCents);
        Assert.False(await checkout.DecideRefundAsync(order, 99, "Changed", false, default));
        Assert.True(await checkout.DecideRefundAsync(order, 55, "Partial", false, default));
        Assert.Equal("re_test", order.StripeRefundId);
        Assert.Equal(GuestSupplierOrderState.Disputed, order.State);
        rows[0].Status = "succeeded";
        await checkout.ReconcileRefundAsync(order, default);
        Assert.Equal(55, order.RefundCents);
        Assert.Equal(5, new GuestSupplierOrders(db, TimeProvider.System).View(order).CommissionRefundCents);
        Assert.False(GuestSupplierRefunds.BlocksPayout(order));
        rows[0].Status = "failed";
        await checkout.ReconcileRefundAsync(order, default);
        Assert.Equal("failed", order.RefundStatus);
        Assert.Equal(0, order.RefundCents);
        Assert.Equal(GuestSupplierOrderState.Disputed, order.State);
        Assert.Single(rows);
    }

    [Theory]
    [InlineData(950, 500L, 1000L, 475)]
    [InlineData(50, 333L, 1000L, 17)]
    [InlineData(50, 1200L, 1000L, 50)]
    [InlineData(50, 1L, 0L, 0)]
    public void BankRecovery_CapsTheProportionalAmount(int paid, long disputed, long available, int expected) =>
        Assert.Equal(expected, GuestSupplierCheckout.ProportionalRecovery(paid, disputed, available));
}
