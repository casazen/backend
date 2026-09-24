using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// BK-08 (A3-14): the Stripe requests of the deferred charge, checked on a mocked <see cref="IStripeClient"/>. The saved
/// payment method is charged off-session on the connected account it was saved on, with an idempotency key, and without
/// the parameter combinations that made PR #447 fail (<c>error_on_requires_action</c> without
/// <c>payment_method_types</c>, a <c>return_url</c>).
/// </summary>
public class StripeServiceDeferredChargeTests
{
    private const string Account = "acct_host_123";

    private readonly Mock<IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, BaseOptions? Options, RequestOptions? RequestOptions)> _requests = [];

    public StripeServiceDeferredChargeTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new PaymentIntent { Id = "pi_1", Status = "succeeded" });
        _client
            .Setup(c => c.RequestAsync<StripeList<PaymentIntent>>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new StripeList<PaymentIntent> { Data = [new PaymentIntent { Id = "pi_1" }], HasMore = false });
    }

    [Fact]
    public async Task ChargePaymentMethodAsync_SavedCard_CreatesAndConfirmsOffSessionOnConnectedAccount()
    {
        var paymentIntent = await Service().ChargePaymentMethodAsync(
            Account,
            "cus_1",
            "pm_1",
            12_345,
            "eur",
            new Dictionary<string, string> { ["kind"] = "direct-booking-deadline-charge" },
            "direct-booking-deadline:b1:20261001:1");

        Assert.Equal("pi_1", paymentIntent.Id);
        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/payment_intents", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("direct-booking-deadline:b1:20261001:1", request.RequestOptions?.IdempotencyKey);
        var options = Assert.IsType<PaymentIntentCreateOptions>(request.Options);
        Assert.Equal("cus_1", options.Customer);
        Assert.Equal("pm_1", options.PaymentMethod);
        Assert.Equal(12_345, options.Amount);
        Assert.True(options.Confirm);
        Assert.True(IsTrue(options.OffSession));
        // With automatic payment methods Stripe refuses error_on_requires_action (needs payment_method_types), and
        // return_url is not needed off-session: none of them is sent (PR #447).
        Assert.Null(options.ErrorOnRequiresAction);
        Assert.Null(options.PaymentMethodTypes);
        Assert.Null(options.ReturnUrl);
        Assert.Equal("direct-booking-deadline-charge", options.Metadata["kind"]);
    }

    [Fact]
    public async Task ConfirmPaymentIntentOffSessionAsync_FailedAttempt_ConfirmsSameIntentOffSession()
    {
        await Service().ConfirmPaymentIntentOffSessionAsync("pi_1", Account, "pm_1", "direct-booking-deadline-confirm:pi_1:2");

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/payment_intents/pi_1/confirm", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("direct-booking-deadline-confirm:pi_1:2", request.RequestOptions?.IdempotencyKey);
        var options = Assert.IsType<PaymentIntentConfirmOptions>(request.Options);
        Assert.Equal("pm_1", options.PaymentMethod);
        Assert.True(IsTrue(options.OffSession));
        Assert.Null(options.ErrorOnRequiresAction);
        Assert.Null(options.ReturnUrl);
    }

    [Fact]
    public async Task ListCustomerPaymentIntentsAsync_ConnectedAccount_ListsTheCustomersIntentsThere()
    {
        var intents = await Service().ListCustomerPaymentIntentsAsync("cus_1", Account);

        Assert.Equal("pi_1", Assert.Single(intents).Id);
        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/payment_intents", request.Path);
        Assert.Equal(Account, request.RequestOptions?.StripeAccount);
        Assert.Equal("cus_1", Assert.IsType<PaymentIntentListOptions>(request.Options).Customer);
    }

    [Fact]
    public async Task ChargePaymentMethodAsync_NoIdempotencyKey_IsRejected()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service().ChargePaymentMethodAsync(
            Account, "cus_1", "pm_1", 100, "eur", new Dictionary<string, string>(), " "));
        Assert.Empty(_requests);
    }

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        null => false,
        _ => string.Equals(value.ToString(), "True", StringComparison.OrdinalIgnoreCase),
    };

    private StripeService Service() => new(NullLogger<StripeService>.Instance, _client.Object);
}
