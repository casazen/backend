using Casazen.Infrastructure.External;
using Microsoft.Extensions.Configuration;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public sealed class StripeRentSepaGatewayTests
{
    [Fact]
    public async Task DestinationChargePreservesFullLandlordDue_AndCollectsOneEuroOnPlatform()
    {
        var client = new Mock<IStripeClient>();
        client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        PaymentIntentCreateOptions? sent = null; RequestOptions? request = null;
        client.Setup(c => c.RequestAsync<PaymentIntent>(HttpMethod.Post, "/v1/payment_intents", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, req, _) => { sent = (PaymentIntentCreateOptions)options; request = req; })
            .ReturnsAsync(new PaymentIntent { Id = "pi_rent" });
        var gateway = new StripeRentSepaGateway(new ConfigurationBuilder().Build(), client.Object);
        await gateway.CreateAsync("acct_owner", Guid.NewGuid(), Guid.NewGuid(), 110100, "rent-unique-key", default);
        Assert.NotNull(sent); Assert.NotNull(request);
        Assert.Equal(110100, sent.Amount); Assert.Equal(100, sent.ApplicationFeeAmount);
        Assert.Equal("acct_owner", sent.TransferData.Destination); Assert.Equal("acct_owner", sent.OnBehalfOf);
        Assert.Null(request.StripeAccount); Assert.Equal("rent-unique-key", request.IdempotencyKey);
        Assert.Equal("2026-10-28.endive", StripeBankLinkGateway.ReadSessionApiVersion(request));
        Assert.Equal(new[] { "sepa_debit" }, Assert.IsType<string[]>(sent.ExtraParams["allowed_payment_method_types"]));
    }
}
