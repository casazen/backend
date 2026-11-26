using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Infrastructure.Data;
using Moq;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

public sealed partial class GuestSupplierStripeSandboxTests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class StripeSandboxFactAttribute : FactAttribute
    {
        public StripeSandboxFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY")?.StartsWith("sk_test_") != true ||
                Environment.GetEnvironmentVariable("STRIPE_TEST_PUBLISHABLE_KEY")?.StartsWith("pk_test_") != true)
                Skip = "Requires opt-in platform keys for the Stripe test environment.";
        }
    }

    [StripeSandboxFact]
    public async Task SandboxCardHold_SignedCallback_CaptureAndRetry()
    {
        var secret = Environment.GetEnvironmentVariable("STRIPE_TEST_SECRET_KEY")!;
        var publishable = Environment.GetEnvironmentVariable("STRIPE_TEST_PUBLISHABLE_KEY")!;
        var webhookKey = $"whsec_{Guid.NewGuid():N}";
        var config = Config(secret, publishable, webhookKey);
        var stripe = new StripeClient(secret);
        var intents = new PaymentIntentService(stripe);
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("gueststripe");
        await using var db = database.CreateContext();
        var clock = TimeProvider.System;
        var now = clock.GetUtcNow().UtcDateTime;
        var order = await SeedOrderAsync(db, now);
        var checkout = new GuestSupplierCheckout(db, clock, config);
        Assert.Null(await checkout.StartAsync(order, 100, default));
        var session = await checkout.StartAsync(order, 110, default);
        Assert.NotNull(session);
        var id = order.StripePaymentIntentId!;
        try
        {
            var retry = await checkout.StartAsync(order, 110, default);
            Assert.True(retry?.ClientSecret == session.ClientSecret);
            using var http = new HttpClient();
            using var confirm = new HttpRequestMessage(HttpMethod.Post, $"https://api.stripe.com/v1/payment_intents/{id}/confirm");
            confirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", publishable);
            confirm.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_secret"] = session.ClientSecret,
                ["payment_method"] = "pm_card_visa"
            });
            using var confirmation = await http.SendAsync(confirm);
            confirmation.EnsureSuccessStatusCode();
            var intent = await intents.GetAsync(id);
            Assert.False(intent.Livemode);
            Assert.Equal("requires_capture", intent.Status);
            using var actualIntent = JsonDocument.Parse(intent.ToJson());
            var json = JsonSerializer.Serialize(new
            {
                id = "evt_sandbox_fixture",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                request = new { id = "req_fixture", idempotency_key = (string?)null },
                pending_webhooks = 1,
                @object = "event",
                type = "payment_intent.amount_capturable_updated",
                api_version = StripeConfiguration.ApiVersion,
                livemode = false,
                data = new { @object = actualIntent.RootElement }
            });
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookKey), Encoding.UTF8.GetBytes($"{timestamp}.{json}"));
            var signature = $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";
            async Task<IActionResult> Callback(string header)
            {
                var context = new DefaultHttpContext();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
                context.Request.Headers["Stripe-Signature"] = header;
                return await new GuestSupplierStripeWebhookController(db, checkout, config)
                { ControllerContext = new ControllerContext { HttpContext = context } }.Receive(default);
            }
            Assert.IsType<BadRequestResult>(await Callback($"t={timestamp},v1=invalid"));
            Assert.Equal(GuestSupplierOrderState.Quoted, order.State);
            Assert.IsType<OkResult>(await Callback(signature));
            Assert.Equal(GuestSupplierOrderState.Authorized, order.State);
            Assert.True(await checkout.SupplierActionAsync(order, "accept", default));
            Assert.True(await checkout.SupplierActionAsync(order, "accept", default));
            Assert.IsType<OkResult>(await Callback(signature));
            Assert.Equal(GuestSupplierOrderState.Accepted, order.State);
            var captured = await intents.GetAsync(id);
            Assert.False(captured.Livemode);
            Assert.Equal("succeeded", captured.Status);
            Assert.Equal(110, captured.AmountReceived);
        }
        finally
        {
            var intent = await intents.GetAsync(id);
            if (!intent.Livemode && intent.Status == "succeeded")
                await new RefundService(stripe).CreateAsync(new RefundCreateOptions { PaymentIntent = id });
            else if (!intent.Livemode && intent.Status != "canceled")
                await intents.CancelAsync(id);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"id":"evt_test","object":"event","livemode":false,"type":"payment_intent.succeeded","data":{}}""")]
    [InlineData("""{"id":"evt_test","object":"event","livemode":true,"type":"payment_intent.succeeded"}""")]
    public async Task SignedMalformedOrLiveEvent_ReturnsBadRequest(string json)
    {
        const string webhookKey = "whsec_synthetic";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookKey), Encoding.UTF8.GetBytes($"{timestamp}.{json}"));
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={Convert.ToHexStringLower(signature)}";
        var controller = new GuestSupplierStripeWebhookController(null!, null!, Config("sk_test_fake", "pk_test_fake", webhookKey))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        Assert.IsType<BadRequestResult>(await controller.Receive(default));
    }

    [PostgresFact]
    public async Task StripeApiRequests_PersistOneIntent_CancelOnDecline_RejectMismatches()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("gueststripemock");
        await using var db = database.CreateContext();
        var order = await SeedOrderAsync(db, DateTime.UtcNow);
        var config = Config("sk_test_synthetic", "pk_test_synthetic", "whsec_synthetic");
        var intent = new PaymentIntent
        {
            Id = "pi_fixture",
            Currency = "eur",
            Amount = 110,
            CaptureMethod = "manual",
            Metadata = new() { ["kind"] = GuestSupplierCheckout.Kind, ["orderId"] = order.Id.ToString("D") },
            ClientSecret = "pi_fixture_secret_fixture",
            Status = "requires_payment_method"
        };
        var client = new Mock<IStripeClient>();
        var createCount = 0;
        var cancelCount = 0;
        client.SetupGet(x => x.ApiBase).Returns("https://api.stripe.com");
        client.Setup(x => x.RequestAsync<PaymentIntent>(It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(),
                It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod method, string path, BaseOptions options, RequestOptions request, CancellationToken _) =>
            {
                if (method == HttpMethod.Post && path == "/v1/payment_intents")
                {
                    createCount++;
                    var args = Assert.IsType<PaymentIntentCreateOptions>(options);
                    Assert.Equal(110, args.Amount);
                    Assert.Equal("manual", args.CaptureMethod);
                    Assert.Equal(new[] { "card" }, args.PaymentMethodTypes);
                    Assert.Null(args.TransferData);
                    Assert.Null(request.StripeAccount);
                    Assert.Equal($"guest-supplier:{order.Id:D}:create", request.IdempotencyKey);
                }
                else if (method == HttpMethod.Post && path.EndsWith("/cancel"))
                {
                    cancelCount++;
                    Assert.Equal($"guest-supplier:{order.Id:D}:cancel", request.IdempotencyKey);
                    intent.Status = "canceled";
                }
                else Assert.Equal(HttpMethod.Get, method);
                return Task.FromResult(intent);
            });
        var checkout = new GuestSupplierCheckout(db, TimeProvider.System, config, client.Object);
        Assert.NotNull(await checkout.StartAsync(order, 110, default));
        Assert.NotNull(await checkout.StartAsync(order, 110, default));
        Assert.Equal(1, createCount);
        intent.Status = "requires_capture";
        intent.AmountCapturable = 110;
        intent.Livemode = true;
        Assert.False(await checkout.ReconcileAsync(order, default));
        intent.Livemode = false;
        intent.Amount = 500;
        Assert.False(await checkout.ReconcileAsync(order, default));
        intent.Amount = 110;
        Assert.True(await checkout.ReconcileAsync(order, default));
        Assert.Equal(GuestSupplierOrderState.Authorized, order.State);
        Assert.True(await checkout.SupplierActionAsync(order, "decline", default));
        Assert.True(await checkout.SupplierActionAsync(order, "decline", default));
        Assert.Equal(1, cancelCount);
        Assert.Equal(GuestSupplierOrderState.Declined, order.State);
        Assert.False(await checkout.SupplierActionAsync(order, "accept", default));

        order = await SeedOrderAsync(db, DateTime.UtcNow);
        intent.Id = "pi_expiry";
        intent.Status = "requires_payment_method";
        intent.Metadata["orderId"] = order.Id.ToString("D");
        Assert.NotNull(await checkout.StartAsync(order, 110, default));
        intent.Status = "requires_capture";
        Assert.True(await checkout.ReconcileAsync(order, default));
        order.RequestedFor = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var job = new GuestSupplierStripeExpiryJob(db, checkout, config, TimeProvider.System, NullLogger<GuestSupplierStripeExpiryJob>.Instance);
        await job.ExecuteAsync(default);
        Assert.Equal(GuestSupplierOrderState.Declined, order.State);
        Assert.Equal(2, cancelCount);

        order.State = GuestSupplierOrderState.Completed;
        order.CompletedAt = DateTime.UtcNow.AddDays(-2);
        Assert.Equal("Completed", new GuestSupplierOrders(db, TimeProvider.System).View(order).State);
    }

    [Theory]
    [InlineData(false, "sk_test_x", "pk_test_x", "whsec_x")]
    [InlineData(true, "sk_live_x", "pk_test_x", "whsec_x")]
    [InlineData(true, "sk_test_x", "pk_live_x", "whsec_x")]
    [InlineData(true, "sk_test_x", "pk_test_x", null)]
    public void IncompleteOrLiveConfigurationKeepsSandboxOff(bool enabled, string secret, string publishable, string? webhook)
        => Assert.False(GuestSupplierCheckout.Enabled(Config(secret, publishable, webhook, enabled)));

    private static async Task<GuestSupplierOrder> SeedOrderAsync(AppDbContext db, DateTime now)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var host = HostScopeScenario.NewOrg("Sandbox", $"sandbox-{suffix}");
        var supplier = HostScopeScenario.NewOrg("Supplier", $"supplier-{suffix}");
        var property = HostScopeScenario.NewProperty(host.Id, $"sandbox-{suffix}", "Sandbox");
        var guest = new Guest { OrgId = host.Id, FirstName = "Test", LastName = "Guest" };
        var booking = new Booking
        {
            OrgId = host.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = RomeCalendar.TodayAt(now),
            CheckOutDate = RomeCalendar.TodayAt(now).AddDays(2),
            NumberOfGuests = 1
        };
        var order = new GuestSupplierOrder
        {
            HostOrgId = host.Id,
            SupplierOrgId = supplier.Id,
            BookingId = booking.Id,
            PropertyId = property.Id,
            OfferId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            StripeSandbox = true,
            ServiceName = "Sandbox",
            SupplierName = "Sandbox",
            SupplierPriceCents = 100,
            CommissionCents = 10,
            CreatedAt = now,
            RequestedFor = now.AddHours(2),
            State = GuestSupplierOrderState.Quoted
        };
        db.AddRange(host, supplier, property, guest, booking, order);
        await db.SaveChangesAsync();
        return order;
    }

    internal static IConfiguration Config(string? secret, string? publishable, string? webhook, bool enabled = true) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{GuestSupplierCheckout.Section}:Enabled"] = enabled.ToString(),
            [$"{GuestSupplierCheckout.Section}:SecretKey"] = secret,
            [$"{GuestSupplierCheckout.Section}:PublishableKey"] = publishable,
            [$"{GuestSupplierCheckout.Section}:WebhookSecret"] = webhook
        }).Build();
}
