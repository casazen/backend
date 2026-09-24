using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// BK-21: the Stripe requests of the checkout hold expiry, checked on a mocked <see cref="IStripeClient"/>. The checkout
/// intents live on the host's connected account (direct charges), so every request carries its <c>Stripe-Account</c>
/// header, and the cancellations carry the idempotency key.
/// </summary>
public class StripeServiceIntentCancellationTests
{
    private const string Account = "acct_host_bk21";

    private readonly Mock<IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, BaseOptions? Options, RequestOptions? RequestOptions)> _requests = [];

    public StripeServiceIntentCancellationTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new PaymentIntent { Id = "pi_hold", Status = "canceled" });
        _client
            .Setup(c => c.RequestAsync<SetupIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new SetupIntent { Id = "seti_hold", Status = "canceled" });
    }

    [Fact]
    public async Task CancelPaymentIntentAsync_ConnectedAccount_SendsStripeAccountIdempotencyKeyAndReason()
    {
        var intent = await Service().CancelPaymentIntentAsync("pi_hold", Account, "checkout-hold-expiry:b:pi_hold:x:none");

        Assert.Equal("canceled", intent.Status);
        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/payment_intents/pi_hold/cancel", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("checkout-hold-expiry:b:pi_hold:x:none", request.RequestOptions?.IdempotencyKey);
        Assert.Equal("abandoned", Assert.IsType<PaymentIntentCancelOptions>(request.Options).CancellationReason);
    }

    [Fact]
    public async Task GetPaymentIntentAsync_ConnectedAccount_ReadsOnThatAccount()
    {
        await Service().GetPaymentIntentAsync("pi_hold", Account);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/payment_intents/pi_hold", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
    }

    [Fact]
    public async Task CancelSetupIntentAsync_ConnectedAccount_SendsStripeAccountAndIdempotencyKey()
    {
        var intent = await Service().CancelSetupIntentAsync("seti_hold", Account, "checkout-hold-expiry:b:seti_hold:x:none");

        Assert.Equal("canceled", intent.Status);
        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/setup_intents/seti_hold/cancel", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("checkout-hold-expiry:b:seti_hold:x:none", request.RequestOptions?.IdempotencyKey);
    }

    [Fact]
    public async Task GetSetupIntentAsync_ConnectedAccount_ReadsOnThatAccount()
    {
        await Service().GetSetupIntentAsync("seti_hold", Account);

        var request = Assert.Single(_requests);
        Assert.Equal("/v1/setup_intents/seti_hold", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
    }

    private StripeService Service() => new(NullLogger<StripeService>.Instance, _client.Object);
}
