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
/// SP-15b: what CasaZen sends to Stripe to refund the payment of a service (<see cref="StripeSupplierPaymentGateway.CreateRefundAsync"/>),
/// checked on a mocked <see cref="IStripeClient"/>: a refund of a direct charge is created on the supplier's account
/// (<c>Stripe-Account</c>, paid from its balance) with <c>refund_application_fee=true</c> when the payment carried a commission (the
/// commission goes back in full for a refund in full, in proportion for a partial one), the key
/// <c>service-charge-refund:{payment}:{n}</c> and <c>metadata.kind = service-charge-refund</c>. Nothing here sets
/// <c>application_fee_amount</c>: that stays in the creation of the PaymentIntent (<see cref="ApplicationFeeArchitectureTests"/>).
/// </summary>
public class StripeSupplierPaymentGatewayRefundTests
{
    private const string Account = "acct_supplier_123";
    private const string IntentId = "pi_service_1";

    private readonly Mock<IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, BaseOptions? Options, RequestOptions? RequestOptions)> _requests = [];

    public StripeSupplierPaymentGatewayRefundTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<Refund>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(() => new Refund
            {
                Id = "re_1",
                Status = "succeeded",
                Amount = 1_500,
                PaymentIntentId = IntentId,
                Metadata = new Dictionary<string, string> { ["kind"] = "service-charge-refund", ["serviceRequestPaymentRefundId"] = "r1" },
            });
        _client
            .Setup(c => c.RequestAsync<StripeList<Refund>>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new StripeList<Refund>
            {
                Data =
                [
                    new Refund { Id = "re_1", Status = "pending", Amount = 1_000, PaymentIntentId = IntentId },
                    new Refund { Id = "re_2", Status = "failed", Amount = 500, PaymentIntentId = IntentId, FailureReason = "lost_or_stolen_card" },
                ],
                HasMore = false,
            });
    }

    private StripeSupplierPaymentGateway Gateway() => new(NullLogger<StripeSupplierPaymentGateway>.Instance, _client.Object);

    private static ServiceChargeRefundRequest Request(bool refundFee = true, long amount = 1_500) =>
        new(
            IntentId,
            Account,
            amount,
            refundFee,
            "service-charge-refund:abc:1",
            new Dictionary<string, string> { ["serviceRequestPaymentRefundId"] = "r1", ["serviceRequestPaymentId"] = "p1" });

    [Fact]
    public async Task CreateRefundAsync_IsCreatedOnTheSuppliersAccount_WithTheCommissionRefunded_TheKeyAndTheMetadata()
    {
        var refund = await Gateway().CreateRefundAsync(Request());

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/refunds", request.Path);
        var options = Assert.IsType<RefundCreateOptions>(request.Options);
        Assert.Equal(IntentId, options.PaymentIntent);
        Assert.Equal(1_500, options.Amount);
        Assert.True(options.RefundApplicationFee);
        // A direct charge has no transfer to reverse.
        Assert.Null(options.ReverseTransfer);
        // The refund is paid from the supplier's balance: the header, and the key of the payment and refund number.
        Assert.Equal(Account, request.RequestOptions!.StripeAccount);
        Assert.Equal("service-charge-refund:abc:1", request.RequestOptions.IdempotencyKey);
        Assert.Equal("service-charge-refund", options.Metadata["kind"]);
        Assert.Equal("r1", options.Metadata["serviceRequestPaymentRefundId"]);
        Assert.Equal("p1", options.Metadata["serviceRequestPaymentId"]);
        Assert.Equal("re_1", refund.Id);
    }

    [Fact]
    public async Task CreateRefundAsync_APaymentWithoutCommission_DoesNotAskStripeToRefundAFee()
    {
        await Gateway().CreateRefundAsync(Request(refundFee: false));

        var options = Assert.IsType<RefundCreateOptions>(Assert.Single(_requests).Options);
        // Null, not false: the parameter is left out of the request.
        Assert.Null(options.RefundApplicationFee);
    }

    [Fact]
    public async Task CreateRefundAsync_TheCallerCannotChangeTheKindOfTheRefund()
    {
        var request = Request() with { Metadata = new Dictionary<string, string> { ["kind"] = "booking-refund" } };

        await Gateway().CreateRefundAsync(request);

        Assert.Equal("service-charge-refund", Assert.IsType<RefundCreateOptions>(Assert.Single(_requests).Options).Metadata["kind"]);
    }

    [Fact]
    public async Task CreateRefundAsync_ReturnsWhatTheServiceReads_WithoutAStripeType()
    {
        var refund = await Gateway().CreateRefundAsync(Request());

        Assert.Equal("re_1", refund.Id);
        Assert.Equal(IntentId, refund.PaymentIntentId);
        Assert.Equal(1_500, refund.AmountCents);
        Assert.Equal("succeeded", refund.Status);
        Assert.Null(refund.FailureReason);
        Assert.Equal("r1", refund.Metadata!["serviceRequestPaymentRefundId"]);
    }

    [Theory]
    [InlineData("", "service-charge-refund:abc:1")]
    [InlineData("acct_x", "")]
    public async Task CreateRefundAsync_WithoutAnAccountOrAKey_IsRefused_NothingIsSent(string account, string key)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Gateway().CreateRefundAsync(Request() with { ConnectedAccountId = account, IdempotencyKey = key }));

        Assert.Empty(_requests);
    }

    [Fact]
    public async Task CreateRefundAsync_AStripeRefusal_IsRethrown_ForTheServiceToClassify()
    {
        _client
            .Setup(c => c.RequestAsync<Refund>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.BadRequest, new StripeError { Code = "charge_already_refunded" }, "already refunded"));

        var ex = await Assert.ThrowsAsync<StripeException>(() => Gateway().CreateRefundAsync(Request()));

        Assert.Equal("charge_already_refunded", ex.StripeError.Code);
    }

    [Fact]
    public async Task ListRefundsAsync_ReadsTheRefundsOfThePaymentIntent_OnTheSuppliersAccount()
    {
        var refunds = await Gateway().ListRefundsAsync(IntentId, Account);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/refunds", request.Path);
        Assert.Equal(IntentId, Assert.IsType<RefundListOptions>(request.Options).PaymentIntent);
        Assert.Equal(Account, request.RequestOptions!.StripeAccount);
        Assert.Equal(["re_1", "re_2"], refunds.Select(r => r.Id).ToArray());
        Assert.Equal(["pending", "failed"], refunds.Select(r => r.Status!).ToArray());
        Assert.Equal("lost_or_stolen_card", refunds[1].FailureReason);
    }

    [Fact]
    public void MapRefund_IsWhatTheWebhookHandsOver_ForTheRefundEvents()
    {
        var mapped = StripeSupplierPaymentGateway.MapRefund(new Refund
        {
            Id = "re_9",
            PaymentIntentId = IntentId,
            Amount = 700,
            Status = "requires_action",
            Metadata = new Dictionary<string, string> { ["a"] = "b" },
        });

        Assert.Equal(("re_9", IntentId, 700L, "requires_action", (string?)null), (mapped.Id, mapped.PaymentIntentId, mapped.AmountCents, mapped.Status, mapped.FailureReason));
        Assert.Equal("b", mapped.Metadata!["a"]);
    }

    [Fact]
    public void MapRefund_WithoutMetadata_HasNone()
    {
        Assert.Null(StripeSupplierPaymentGateway.MapRefund(new Refund { Id = "re_9", PaymentIntentId = IntentId, Amount = 1, Status = "succeeded", Metadata = null! }).Metadata);
    }
}
