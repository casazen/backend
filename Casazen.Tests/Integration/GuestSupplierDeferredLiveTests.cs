using Casazen.Core.Entities;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Integration.Postgres;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed partial class GuestSupplierStripeSandboxTests
{
    [StripeSandboxFact]
    public Task LiveDeferredQuote_SetupAndOffSessionCharge_RepeatedAcceptanceKeepsOneIntent() =>
        VerifyLiveDeferredQuoteAsync("pm_card_visa", declineFirst: false);

    [StripeSandboxFact]
    public Task LiveDeferredQuote_DeclinedOffSessionCharge_RetryOnSameIntent() =>
        VerifyLiveDeferredQuoteAsync("pm_card_chargeCustomerFail", declineFirst: true);

    private static async Task VerifyLiveDeferredQuoteAsync(string testMethod, bool declineFirst)
    {
        var secret = Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY")!;
        var publishable = Environment.GetEnvironmentVariable("STRIPE_TEST_PUBLISHABLE_KEY")!;
        var stripe = new StripeClient(secret);
        var setups = new SetupIntentService(stripe);
        var intents = new PaymentIntentService(stripe);
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("guestquotedcharge");
        await using var db = database.CreateContext();
        var now = DateTime.UtcNow;
        var order = await SeedOrderAsync(db, now);
        order.RequestedFor = now.AddDays(12);
        order.ExpectedSupplierConfirmationAt = now.AddDays(9);
        order.StripeDeferred = GuestSupplierCheckout.NeedsSavedCard(now, order.RequestedFor, order.ExpectedSupplierConfirmationAt);
        order.State = GuestSupplierOrderState.PendingQuote;
        order.SupplierPriceCents = 0;
        order.CommissionCents = 0;
        order.CommissionBasisPoints = 1000;
        await db.SaveChangesAsync();
        var checkout = new GuestSupplierCheckout(db, TimeProvider.System, Config(secret, publishable, "whsec_local_test"));
        Assert.Null(await checkout.StartAsync(order, 110, default, GuestSupplierCheckout.ConsentVersion));
        Assert.True(GuestSupplierOrders.Quote(order, 100, now));
        await db.SaveChangesAsync();
        try
        {
            Assert.Null(await checkout.StartAsync(order, 110, default));
            var session = await checkout.StartAsync(order, 110, default, GuestSupplierCheckout.ConsentVersion);
            Assert.Equal("setup", session?.Mode);
            var setup = await setups.ConfirmAsync(order.StripeSetupIntentId!, new SetupIntentConfirmOptions { PaymentMethod = testMethod });
            Assert.False(setup.Livemode);
            Assert.Equal("succeeded", setup.Status);
            Assert.True(await checkout.ReconcileAsync(order, default));
            Assert.Equal(GuestSupplierOrderState.CardSaved, order.State);
            Assert.True(await checkout.SupplierActionAsync(order, "accept", default));
            var paymentId = order.StripePaymentIntentId!;
            if (declineFirst)
            {
                Assert.Equal(GuestSupplierOrderState.PaymentFailed, order.State);
                var retry = await checkout.StartAsync(order, 110, default);
                Assert.Equal("payment", retry?.Mode);
                var paid = await intents.ConfirmAsync(paymentId, new PaymentIntentConfirmOptions { PaymentMethod = "pm_card_visa" });
                Assert.False(paid.Livemode);
                Assert.True(await checkout.ReconcileAsync(order, default));
            }
            Assert.True(await checkout.SupplierActionAsync(order, "accept", default));
            Assert.Equal(paymentId, order.StripePaymentIntentId);
            Assert.Equal(GuestSupplierOrderState.Accepted, order.State);
            var final = await intents.GetAsync(paymentId);
            Assert.False(final.Livemode);
            Assert.Equal(110, final.AmountReceived);
        }
        finally
        {
            if (order.StripePaymentIntentId is { } id)
            {
                var pi = await intents.GetAsync(id);
                if (!pi.Livemode && pi.Status == "succeeded")
                    await new RefundService(stripe).CreateAsync(new RefundCreateOptions { PaymentIntent = id });
                else if (!pi.Livemode && pi.Status != "canceled") await intents.CancelAsync(id);
            }
            if (order.StripeSetupIntentId is { } setupId)
            {
                var setup = await setups.GetAsync(setupId);
                if (!setup.Livemode && setup.Status is not ("succeeded" or "canceled")) await setups.CancelAsync(setupId);
            }
            if (order.StripeCustomerId is { } customerId) await new CustomerService(stripe).DeleteAsync(customerId);
        }
    }
}
