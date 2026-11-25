using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Casazen.Core.Exceptions;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Controllers;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public sealed class GuestHostExtraTests
{
    [Fact]
    public void PriceValidationUsesTheSameDecimalRangeInItalian()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            var priceParameter = typeof(GuestHostExtrasController.ExtraInput).GetConstructors().Single()
                .GetParameters().Single(parameter => parameter.Name == "Price");
            var range = priceParameter.GetCustomAttribute<RangeAttribute>()!;

            Assert.True(range.IsValid(0.50m));
            Assert.True(range.IsValid(15.25m));
            Assert.True(range.IsValid(10000m));
            Assert.False(range.IsValid(0.49m));
            Assert.False(range.IsValid(10000.01m));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task ApprovalGatesConnectedPaymentAndMatchingWebhookAllowsFulfilment()
    {
        using var world = new GuestPortalTestWorld();
        var portal = await OpenAsync(world);
        var stripe = new Mock<IStripeService>(MockBehavior.Strict);
        var service = Service(world, stripe);
        var (extra, _) = await service.SaveAsync(portal.OrgId, portal.PropertyId, Guid.Empty, new GuestHostExtra
        {
            Name = "Culla e kit bimbo",
            Price = 15m,
            RequiresApproval = true,
            Pricing = GuestHostExtraPricing.PerNight,
            Inventory = 1
        }, true, _ => Task.FromResult(true), default);

        var requestId = Guid.NewGuid();
        var order = await service.OrderAsync(portal, extra!.Value, 1, requestId, default);
        Assert.Equal(45m, order.Amount);
        Assert.Equal(GuestHostExtraStatus.AwaitingAuthorization, order.Status);
        Assert.Equal(order, await service.OrderAsync(portal, extra.Value, 1, requestId, default));
        await Assert.ThrowsAsync<DomainConflictException>(() => service.ChangeStatusAsync(portal.OrgId, portal.BookingId,
            order.Id, GuestHostExtraStatus.CapturePending, true, default));
        Assert.Empty((await service.ListAsync(portal, default)).Offers);

        var intent = new PaymentIntent
        {
            Id = "pi_extra",
            ClientSecret = "pi_extra_secret_mock",
            Status = "requires_payment_method",
            Currency = "eur",
            Amount = 4500,
            CaptureMethod = "manual",
            Metadata = new() { ["order_id"] = order.Id.ToString("D"), ["org_id"] = portal.OrgId.ToString("D") },
        };
        stripe.Setup(x => x.CreateConnectedAuthorizationAsync(
                "acct_extra", 4500, "eur", It.Is<Dictionary<string, string>>(m => m["kind"] == "guest-host-extra"),
                $"guest-host-extra:{order.Id:N}", "Culla e kit bimbo", default))
            .ReturnsAsync(intent);
        Assert.Equal("acct_extra", (await service.PayAsync(portal, order.Id, default)).StripeAccountId);
        intent.Status = "requires_capture";
        intent.AmountCapturable = 4500;
        await service.ObserveAsync(intent, "acct_other", default);
        Assert.Equal(GuestHostExtraStatus.AwaitingAuthorization, Assert.Single((await service.ListAsync(portal, default)).Orders).Status);
        await service.ObserveAsync(intent, "acct_extra", default);
        Assert.Equal(GuestHostExtraStatus.Requested, Assert.Single((await service.ListAsync(portal, default)).Orders).Status);

        stripe.Setup(x => x.GetPaymentIntentAsync(intent.Id, "acct_extra", default)).ReturnsAsync(intent);
        stripe.Setup(x => x.CaptureConnectedAuthorizationAsync("acct_extra", intent.Id,
                $"guest-host-extra-capture:{order.Id:N}", default))
            .ReturnsAsync(new PaymentIntent { Id = intent.Id, Currency = "eur", Status = "processing" });
        Assert.True(await service.ChangeStatusAsync(portal.OrgId, portal.BookingId, order.Id,
            GuestHostExtraStatus.CapturePending, true, default));
        Assert.Equal(GuestHostExtraStatus.CapturePending, Assert.Single((await service.ListAsync(portal, default)).Orders).Status);

        intent.Status = "succeeded";
        intent.AmountCapturable = 0;
        intent.AmountReceived = 4500;
        await service.ObserveAsync(intent, "acct_extra", default);
        Assert.True(await service.ChangeStatusAsync(portal.OrgId, portal.BookingId, order.Id,
            GuestHostExtraStatus.Fulfilled, true, default));
        await service.ObserveAsync(intent, "acct_extra", default);
        Assert.Equal(GuestHostExtraStatus.Fulfilled, Assert.Single((await service.ListAsync(portal, default)).Orders).Status);
    }

    [Fact]
    public async Task PaidServicesPolicyAppliesToCatalogAndDirectOrderRequests()
    {
        using var world = new GuestPortalTestWorld();
        var portal = await OpenAsync(world);
        var policy = new Mock<IGuestPortalPolicy>();
        policy.Setup(x => x.CanShowPaidServicesAsync(portal, default)).ReturnsAsync(false);
        var service = Service(world, new Mock<IStripeService>(MockBehavior.Strict), policy.Object);
        var (extra, _) = await service.SaveAsync(portal.OrgId, portal.PropertyId, Guid.Empty,
            new GuestHostExtra { Name = "Kit", Price = 15m }, true, _ => Task.FromResult(true), default);
        Assert.Empty((await service.ListAsync(portal, default)).Offers);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.OrderAsync(portal, extra!.Value, 1, Guid.NewGuid(), default));
    }

    private static async Task<PortalContext> OpenAsync(GuestPortalTestWorld world)
    {
        world.Org.ConnectChargesEnabled = true;
        world.Org.StripeConnectedAccountId = "acct_extra";
        world.Db.Orgs.Update(world.Org);
        await world.Db.SaveChangesAsync();
        world.Db.ChangeTracker.Clear();
        return await world.ContextAsync();
    }

    private static GuestHostExtraService Service(GuestPortalTestWorld world, Mock<IStripeService> stripe,
        params IGuestPortalPolicy[] policies) =>
        new(world.Db, stripe.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:PublishableKey"] = "pk_test_mock" }).Build(),
            policies, world.Clock, world.Logs.CreateLogger<GuestHostExtraService>());
}
