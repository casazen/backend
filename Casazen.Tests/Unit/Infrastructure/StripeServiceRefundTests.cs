using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// BK-02 (A3-05): the Stripe requests of refunds and cancellations, checked on a mocked <see cref="IStripeClient"/>.
/// Direct charges live on the host's connected account, so every request must carry its <c>Stripe-Account</c> header,
/// and writes carry an idempotency key.
/// </summary>
public class StripeServiceRefundTests
{
    private const string Account = "acct_host_123";
    private const string PaymentIntentId = "pi_test_direct";

    private readonly Mock<IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, BaseOptions? Options, RequestOptions? RequestOptions)> _requests = [];

    public StripeServiceRefundTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<Refund>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new Refund { Id = "re_1", Status = "succeeded", Amount = 12_345, PaymentIntentId = PaymentIntentId });
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new PaymentIntent { Id = PaymentIntentId, Status = "canceled" });
        _client
            .Setup(c => c.RequestAsync<SetupIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new SetupIntent { Id = "seti_1", Status = "canceled" });
        _client
            .Setup(c => c.RequestAsync<StripeList<Refund>>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new StripeList<Refund>
            {
                Data = [new Refund { Id = "re_1", Status = "pending" }, new Refund { Id = "re_2", Status = "succeeded" }],
                HasMore = false,
            });
    }

    [Fact]
    public async Task CreateRefundAsync_ConnectedPaymentIntent_SendsStripeAccountIdempotencyKeyAndAmount()
    {
        var refund = await Service().CreateRefundAsync(new StripeRefundCreateRequest(
            PaymentIntentId,
            Account,
            12_345,
            "payment-refund:abc",
            new Dictionary<string, string> { ["paymentRefundId"] = "abc" }));

        Assert.Equal("re_1", refund.Id);
        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/refunds", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("payment-refund:abc", request.RequestOptions?.IdempotencyKey);
        var options = Assert.IsType<RefundCreateOptions>(request.Options);
        Assert.Equal(PaymentIntentId, options.PaymentIntent);
        Assert.Equal(12_345, options.Amount);
        Assert.Equal("abc", options.Metadata["paymentRefundId"]);
        // Direct charge without application fee: nothing to reverse or give back on the platform side.
        Assert.Null(options.ReverseTransfer);
        Assert.Null(options.RefundApplicationFee);
    }

    [Fact]
    public async Task CreateRefundAsync_PlatformPaymentIntent_SendsNoStripeAccount()
    {
        await Service().CreateRefundAsync(new StripeRefundCreateRequest(
            PaymentIntentId, null, 500, "payment-refund:platform", new Dictionary<string, string>()));

        Assert.Null(Assert.Single(_requests).RequestOptions?.StripeAccount);
    }

    [Fact]
    public async Task CancelPaymentIntentAsync_Uncollected_CancelsOnConnectedAccountWithIdempotencyKey()
    {
        await Service().CancelPaymentIntentAsync(PaymentIntentId, Account, "booking-cancel:b1:pi");

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/v1/payment_intents/{PaymentIntentId}/cancel", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("booking-cancel:b1:pi", request.RequestOptions?.IdempotencyKey);
    }

    [Fact]
    public async Task CancelSetupIntentAsync_Unconfirmed_CancelsOnConnectedAccount()
    {
        await Service().CancelSetupIntentAsync("seti_1", Account, "booking-cancel:b1:seti");

        var request = Assert.Single(_requests);
        Assert.Equal("/v1/setup_intents/seti_1/cancel", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("booking-cancel:b1:seti", request.RequestOptions?.IdempotencyKey);
    }

    [Fact]
    public async Task ListRefundsAsync_ConnectedAccount_ListsRefundsOfThePaymentIntentThere()
    {
        var refunds = await Service().ListRefundsAsync(PaymentIntentId, Account);

        Assert.Equal(["re_1", "re_2"], refunds.Select(r => r.Id));
        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/refunds", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal(PaymentIntentId, Assert.IsType<RefundListOptions>(request.Options).PaymentIntent);
    }

    [Fact]
    public async Task GetPaymentIntentAsync_ConnectedAccount_ReadsFromThatAccount()
    {
        await Service().GetPaymentIntentAsync(PaymentIntentId, Account);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/v1/payment_intents/{PaymentIntentId}", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
    }

    private StripeService Service() => new(NullLogger<StripeService>.Instance, _client.Object);
}
