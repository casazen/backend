using Casazen.Core.Entities;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Integration.Postgres;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed partial class GuestSupplierStripeSandboxTests
{
    [Theory]
    [InlineData(30, 180, false)]
    [InlineData(30, 10080, false)]
    [InlineData(30, 10081, true)]
    [InlineData(1, 20000, false)]
    [InlineData(1, null, false)]
    [InlineData(15, null, true)]
    public void ConfirmationCommitmentDeterminesWhetherToSaveTheCard(int serviceInDays, int? responseMinutes, bool save)
    {
        var now = new DateTime(2026, 11, 26, 15, 0, 0, DateTimeKind.Utc);
        var service = now.AddDays(serviceInDays);
        var confirmation = GuestSupplierCheckout.ExpectedConfirmation(now, service, responseMinutes);
        Assert.Equal(save, GuestSupplierCheckout.NeedsSavedCard(now, service, confirmation));
    }

    [PostgresFact]
    public async Task SetupConsent_SupplierAcceptance_BankChallengeAndRetry_UseOneCharge()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("guestdeferred");
        await using var db = database.CreateContext();
        var order = await SeedOrderAsync(db, DateTime.UtcNow);
        order.StripeDeferred = true;
        order.RequestedFor = order.CreatedAt.AddDays(12);
        await db.SaveChangesAsync();
        var metadata = new Dictionary<string, string> { ["kind"] = GuestSupplierCheckout.Kind, ["orderId"] = order.Id.ToString("D") };
        var setup = new SetupIntent
        {
            Id = "seti_example",
            CustomerId = "cus_example",
            Metadata = metadata,
            Usage = "off_session",
            Status = "requires_payment_method",
            ClientSecret = "seti_example_secret"
        };
        var charge = new PaymentIntent
        {
            Id = "pi_deferred",
            CustomerId = "cus_example",
            CaptureMethod = "automatic",
            Amount = 110,
            Currency = "eur",
            Metadata = metadata,
            Status = "requires_confirmation",
            ClientSecret = "pi_deferred_secret"
        };
        var client = new Mock<IStripeClient>();
        client.SetupGet(x => x.ApiBase).Returns("https://api.stripe.com");
        client.Setup(x => x.RequestAsync<Customer>(It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Customer { Id = "cus_example", Metadata = metadata });
        client.Setup(x => x.RequestAsync<SetupIntent>(It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(),
            It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(setup);
        var chargeCreates = 0;
        var confirmations = 0;
        client.Setup(x => x.RequestAsync<PaymentIntent>(It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(),
                It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod method, string path, BaseOptions options, RequestOptions request, CancellationToken _) =>
            {
                if (method == HttpMethod.Post && path == "/v1/payment_intents")
                {
                    chargeCreates++;
                    var args = Assert.IsType<PaymentIntentCreateOptions>(options);
                    Assert.NotEqual(true, args.Confirm);
                    Assert.Equal("pm_saved", args.PaymentMethod);
                    Assert.Equal(order.StripeCustomerId, args.Customer);
                    Assert.Null(args.TransferData);
                }
                if (method == HttpMethod.Post && path.EndsWith("/confirm"))
                {
                    confirmations++;
                    Assert.Equal(true, Assert.IsType<PaymentIntentConfirmOptions>(options).OffSession);
                    charge.Status = "requires_action";
                }
                return Task.FromResult(charge);
            });
        var checkout = new GuestSupplierCheckout(db, TimeProvider.System, Config("sk_test_mock", "pk_test_mock", "whsec_mock"), client.Object);
        Assert.Null(await checkout.StartAsync(order, 110, default));
        Assert.Null(await checkout.StartAsync(order, 109, default, GuestSupplierCheckout.ConsentVersion));
        Assert.Equal("setup", (await checkout.StartAsync(order, 110, default, GuestSupplierCheckout.ConsentVersion))?.Mode);
        Assert.NotNull(order.StripeSetupConsentAt);
        Assert.Null(order.StripePaymentIntentId);
        setup.Status = "succeeded";
        setup.PaymentMethodId = "pm_saved";
        Assert.True(await checkout.ReconcileAsync(order, default));
        Assert.Equal(GuestSupplierOrderState.CardSaved, order.State);
        Assert.True(await checkout.SupplierActionAsync(order, "accept", default));
        Assert.Equal(GuestSupplierOrderState.PaymentActionRequired, order.State);
        Assert.Equal("next-action", (await checkout.StartAsync(order, 110, default))?.Mode);
        charge.Status = "requires_payment_method";
        Assert.True(await checkout.ReconcileAsync(order, default));
        Assert.Equal(GuestSupplierOrderState.PaymentFailed, order.State);
        Assert.Equal("payment", (await checkout.StartAsync(order, 110, default))?.Mode);
        charge.AmountReceived = 110;
        charge.Status = "succeeded";
        Assert.True(await checkout.ReconcileAsync(order, default));
        Assert.Equal(GuestSupplierOrderState.Accepted, order.State);
        Assert.True(await checkout.SupplierActionAsync(order, "accept", default));
        Assert.Equal(1, chargeCreates);
        Assert.Equal(1, confirmations);
    }
}
