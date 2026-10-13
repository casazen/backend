using System.Net;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15a: what CasaZen sends to Stripe for the payment of a service (<see cref="StripeSupplierPaymentGateway"/>), checked on a
/// mocked <see cref="IStripeClient"/>: a direct charge on the supplier's account (<c>Stripe-Account</c>), the commission as
/// <c>application_fee_amount</c> only when it is a real one (never an explicit <c>0</c>, A3-40), <c>metadata.kind = service-charge</c>
/// and the idempotency key. The guarantee that the other PaymentIntents carry no fee stays in
/// <see cref="StripeServiceApplicationFeeTests"/>, which this task does not touch.
/// </summary>
public class StripeSupplierPaymentGatewayTests
{
    private const string Account = "acct_supplier_123";
    private const string IntentId = "pi_service_1";

    private readonly Mock<IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, BaseOptions? Options, RequestOptions? RequestOptions)> _requests = [];
    private PaymentIntent _response = New("requires_payment_method");

    public StripeSupplierPaymentGatewayTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(() => _response);
    }

    private static PaymentIntent New(string status, long amount = 6_000, long? fee = 600) => new()
    {
        Id = IntentId,
        Status = status,
        Amount = amount,
        Currency = "eur",
        ApplicationFeeAmount = fee,
        ClientSecret = "pi_service_1_secret_abc",
    };

    private static ServiceChargeIntentRequest Request(
        long amount = 6_000,
        long? fee = 600,
        IReadOnlyDictionary<string, string>? metadata = null,
        string key = "service-charge:abc:1") =>
        new(
            Account,
            amount,
            "eur",
            fee,
            metadata ?? new Dictionary<string, string> { ["serviceRequestPaymentId"] = "p1", ["serviceRequestId"] = "r1", ["supplierOrgId"] = "s1" },
            key,
            "Service request r1");

    private StripeSupplierPaymentGateway Gateway() => new(NullLogger<StripeSupplierPaymentGateway>.Instance, _client.Object);

    // ─── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_DirectCharge_IsCreatedOnTheSupplierAccountWithTheFeeTheMetadataAndTheKey()
    {
        await Gateway().CreateAsync(Request());

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/payment_intents", request.Path);
        var options = Assert.IsType<PaymentIntentCreateOptions>(request.Options);
        Assert.Equal(6_000, options.Amount);
        Assert.Equal("eur", options.Currency);
        Assert.Equal(600, options.ApplicationFeeAmount);
        Assert.True(options.AutomaticPaymentMethods?.Enabled);
        Assert.Equal("Service request r1", options.Description);
        // The charge is on the supplier's account: the header Stripe-Account, the idempotency key of the payment and attempt.
        Assert.Equal(Account, request.RequestOptions!.StripeAccount);
        Assert.Equal("service-charge:abc:1", request.RequestOptions.IdempotencyKey);
        // Routed by the webhook of SP-15b on metadata.kind, with the ids that find the payment.
        Assert.Equal("service-charge", options.Metadata["kind"]);
        Assert.Equal("p1", options.Metadata["serviceRequestPaymentId"]);
        Assert.Equal("r1", options.Metadata["serviceRequestId"]);
        Assert.Equal("s1", options.Metadata["supplierOrgId"]);
        // A direct charge: no transfer, no destination, no on-behalf-of.
        Assert.Null(options.TransferData);
        Assert.Null(options.OnBehalfOf);
    }

    [Fact]
    public async Task CreateAsync_ReturnsWhatTheServiceReads_WithoutAStripeType()
    {
        var intent = await Gateway().CreateAsync(Request());

        Assert.Equal(IntentId, intent.Id);
        Assert.Equal("requires_payment_method", intent.Status);
        Assert.Equal(6_000, intent.AmountCents);
        Assert.Equal("eur", intent.Currency);
        Assert.Equal(600, intent.ApplicationFeeCents);
        Assert.Equal("pi_service_1_secret_abc", intent.ClientSecret);
        Assert.Equal(Account, intent.ConnectedAccountId);
        Assert.Null(intent.LastErrorCode);
    }

    [Theory]
    [InlineData(null)] // none to send
    [InlineData(0L)] // an explicit zero is never sent (A3-40)
    [InlineData(-5L)]
    [InlineData(6_000L)] // not strictly below the amount: Stripe would refuse it
    [InlineData(9_999L)]
    public async Task CreateAsync_WithoutARealFee_LeavesApplicationFeeAmountUnset(long? fee)
    {
        await Gateway().CreateAsync(Request(fee: fee));

        var options = Assert.IsType<PaymentIntentCreateOptions>(Assert.Single(_requests).Options);
        // Null, not 0: the parameter is omitted from the request.
        Assert.Null(options.ApplicationFeeAmount);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5_999L)]
    public async Task CreateAsync_FeeStrictlyBetweenZeroAndTheAmount_IsSent(long fee)
    {
        await Gateway().CreateAsync(Request(fee: fee));

        Assert.Equal(fee, Assert.IsType<PaymentIntentCreateOptions>(Assert.Single(_requests).Options).ApplicationFeeAmount);
    }

    [Fact]
    public async Task CreateAsync_TheCallerCannotChangeTheKindOfTheCharge()
    {
        var metadata = new Dictionary<string, string> { ["kind"] = "direct-booking", ["serviceRequestPaymentId"] = "p1" };

        await Gateway().CreateAsync(Request(metadata: metadata));

        var options = Assert.IsType<PaymentIntentCreateOptions>(Assert.Single(_requests).Options);
        Assert.Equal("service-charge", options.Metadata["kind"]);
        // The caller's dictionary was not modified.
        Assert.Equal("direct-booking", metadata["kind"]);
    }

    [Fact]
    public async Task CreateAsync_WithoutAnAccountOrAKey_IsRefusedBeforeAnyCall()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateAsync(Request() with { ConnectedAccountId = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateAsync(Request() with { IdempotencyKey = "" }));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task CreateAsync_StripeRefuses_TheStripeExceptionReachesTheCaller()
    {
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, "/v1/payment_intents", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.BadRequest, new StripeError { Type = "invalid_request_error", Code = "amount_too_small" }, "too small"));

        // The API turns it into 503 payment_provider_error (ErrorHandlingMiddleware); the gateway does not hide it.
        await Assert.ThrowsAsync<StripeException>(() => Gateway().CreateAsync(Request()));
    }

    // ─── Get and cancel ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReadsThePaymentIntentFromTheSupplierAccount()
    {
        _response = New("processing");
        _response.LastPaymentError = new StripeError { Code = "card_declined" };

        var intent = await Gateway().GetAsync(IntentId, Account);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/v1/payment_intents/{IntentId}", request.Path);
        Assert.Equal(Account, request.RequestOptions!.StripeAccount);
        Assert.Equal("processing", intent.Status);
        Assert.Equal("card_declined", intent.LastErrorCode);
    }

    [Fact]
    public async Task CancelAsync_CancelsOnTheSupplierAccountWithTheKey()
    {
        _response = New("canceled");

        var intent = await Gateway().CancelAsync(IntentId, Account, "service-charge-cancel:abc:pi_service_1");

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/v1/payment_intents/{IntentId}/cancel", request.Path);
        Assert.Equal(Account, request.RequestOptions!.StripeAccount);
        Assert.Equal("service-charge-cancel:abc:pi_service_1", request.RequestOptions.IdempotencyKey);
        Assert.Equal("canceled", intent.Status);
    }

    [Fact]
    public async Task CancelAsync_APaymentIntentThatChangedState_IsReturnedAsItIsNotAnError()
    {
        // Paid between the read and the cancellation: Stripe answers payment_intent_unexpected_state; the caller must see the real state.
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, $"/v1/payment_intents/{IntentId}/cancel", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(
                HttpStatusCode.BadRequest,
                new StripeError { Type = "invalid_request_error", Code = "payment_intent_unexpected_state" },
                "cannot be canceled"));
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Get, $"/v1/payment_intents/{IntentId}", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(New("succeeded"));

        var intent = await Gateway().CancelAsync(IntentId, Account, "service-charge-cancel:abc:pi_service_1");

        Assert.Equal("succeeded", intent.Status);
    }

    [Fact]
    public async Task CancelAsync_AnyOtherStripeError_ReachesTheCaller()
    {
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, $"/v1/payment_intents/{IntentId}/cancel", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        await Assert.ThrowsAsync<StripeException>(() => Gateway().CancelAsync(IntentId, Account, "key"));
    }

    [Fact]
    public void TheKeysAndKindOfAServicePayment_AreStable()
    {
        var id = Guid.Parse("3f2c1a40-7c11-4b53-8e8a-0c2f4d9a1b11");

        Assert.Equal("service-charge", ServiceCharges.Kind);
        Assert.Equal("service-charge:3f2c1a407c114b538e8a0c2f4d9a1b11:2", ServiceCharges.CreationIdempotencyKey(id, 2));
        Assert.Equal("service-charge-cancel:3f2c1a407c114b538e8a0c2f4d9a1b11:pi_1", ServiceCharges.CancellationIdempotencyKey(id, "pi_1"));
    }

    [Theory]
    [InlineData("requires_payment_method", true)]
    [InlineData("requires_confirmation", true)]
    [InlineData("requires_action", true)]
    [InlineData("processing", false)]
    [InlineData("succeeded", false)]
    [InlineData("requires_capture", false)]
    [InlineData("canceled", false)]
    [InlineData(null, false)]
    public void IsPayable_OnlyBeforeAnythingWasCollected(string? status, bool expected)
    {
        Assert.Equal(expected, ServiceCharges.IsPayable(status));
    }
}
