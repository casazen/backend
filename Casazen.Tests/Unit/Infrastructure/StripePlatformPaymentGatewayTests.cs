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
/// SP-15c-a: what CasaZen sends to Stripe for the money it holds (<see cref="StripePlatformPaymentGateway"/>), checked on a mocked
/// <see cref="IStripeClient"/>: the PaymentIntent is on the platform (no <c>Stripe-Account</c>), blocks a card only (<c>capture_method =
/// manual</c>, <c>payment_method_types = [card]</c>), carries the transfer group and <b>no application fee</b>; a charge or a cancellation that
/// meets another state returns the real one; the transfer to the supplier has <c>source_transaction</c>, the destination, the group and a key
/// of its own. Nothing here reaches Stripe.
/// </summary>
public class StripePlatformPaymentGatewayTests
{
    private const string IntentId = "pi_held_1";
    private const string ChargeId = "ch_held_1";
    private const string Group = "service-hold-abc";

    private readonly Mock<IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, BaseOptions? Options, RequestOptions? RequestOptions)> _requests = [];
    private PaymentIntent _intent = New("requires_payment_method");

    public StripePlatformPaymentGatewayTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(() => _intent);
    }

    private static PaymentIntent New(string status, long amount = 6_000) => new()
    {
        Id = IntentId,
        Status = status,
        Amount = amount,
        AmountCapturable = status == "requires_capture" ? amount : 0,
        AmountReceived = status == "succeeded" ? amount : 0,
        Currency = "eur",
        ClientSecret = "pi_held_1_secret_abc",
        LatestChargeId = status is "requires_capture" or "succeeded" ? ChargeId : null,
        TransferGroup = Group,
        Metadata = new Dictionary<string, string> { ["showcaseHoldId"] = "h1" },
    };

    private static PlatformChargeRequest Request(bool manual = true, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            6_000,
            "eur",
            manual,
            Group,
            metadata ?? new Dictionary<string, string> { ["showcaseHoldId"] = "h1", ["supplierOrgId"] = "s1" },
            "service-hold:abc",
            "Booking of a service");

    private StripePlatformPaymentGateway Gateway() => new(NullLogger<StripePlatformPaymentGateway>.Instance, _client.Object);

    // ─── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_BlocksTheCardOnThePlatform_WithTheGroupTheMetadataAndTheKey_AndNoFee()
    {
        await Gateway().CreateAsync(Request());

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/payment_intents", request.Path);
        var options = Assert.IsType<PaymentIntentCreateOptions>(request.Options);
        Assert.Equal(6_000, options.Amount);
        Assert.Equal("eur", options.Currency);
        Assert.Equal("manual", options.CaptureMethod);
        Assert.Equal(new[] { "card" }, options.PaymentMethodTypes);
        Assert.Equal(Group, options.TransferGroup);
        Assert.Equal("Booking of a service", options.Description);

        // On the platform: no connected account, no destination, no on-behalf-of. The commission is what is not transferred, never a fee.
        Assert.Null(request.RequestOptions!.StripeAccount);
        Assert.Equal("service-hold:abc", request.RequestOptions.IdempotencyKey);
        Assert.Null(options.ApplicationFeeAmount);
        Assert.Null(options.TransferData);
        Assert.Null(options.OnBehalfOf);
        Assert.Null(options.AutomaticPaymentMethods);

        // Routed by the webhook on the kind, with the marker of the money CasaZen holds and the ids that find the hold.
        Assert.Equal("service-charge", options.Metadata["kind"]);
        Assert.Equal("platform-held", options.Metadata["funds"]);
        Assert.Equal("h1", options.Metadata["showcaseHoldId"]);
        Assert.Equal("s1", options.Metadata["supplierOrgId"]);
    }

    [Fact]
    public async Task CreateAsync_ForACardChargedAtOnce_LeavesTheCaptureAutomatic()
    {
        await Gateway().CreateAsync(Request(manual: false));

        var options = Assert.IsType<PaymentIntentCreateOptions>(Assert.Single(_requests).Options);
        Assert.Null(options.CaptureMethod);
        Assert.Equal(new[] { "card" }, options.PaymentMethodTypes);
        Assert.Null(options.ApplicationFeeAmount);
    }

    [Fact]
    public async Task CreateAsync_TheCallerCannotChangeTheKindOfTheCharge_NorTheFundsMarker()
    {
        var metadata = new Dictionary<string, string> { ["kind"] = "direct-booking", ["funds"] = "direct" };

        await Gateway().CreateAsync(Request(metadata: metadata));

        var options = Assert.IsType<PaymentIntentCreateOptions>(Assert.Single(_requests).Options);
        Assert.Equal("service-charge", options.Metadata["kind"]);
        Assert.Equal("platform-held", options.Metadata["funds"]);
        Assert.Equal("direct-booking", metadata["kind"]);
    }

    [Fact]
    public async Task CreateAsync_ReturnsWhatTheServiceReads_WithoutAStripeType()
    {
        var charge = await Gateway().CreateAsync(Request());

        Assert.Equal(IntentId, charge.Id);
        Assert.Equal("requires_payment_method", charge.Status);
        Assert.Equal(6_000, charge.AmountCents);
        Assert.Equal("eur", charge.Currency);
        Assert.Null(charge.ApplicationFeeCents);
        Assert.Equal("pi_held_1_secret_abc", charge.ClientSecret);
        Assert.Equal(Group, charge.TransferGroup);
        Assert.Equal("h1", charge.Metadata["showcaseHoldId"]);
    }

    [Fact]
    public async Task CreateAsync_WithoutAKeyOrAGroup_IsRefusedBeforeAnyCall()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateAsync(Request() with { IdempotencyKey = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateAsync(Request() with { TransferGroup = " " }));
        Assert.Empty(_requests);
    }

    // ─── Capture ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_ChargesTheWholeBlock_OnThePlatform_WithTheKey()
    {
        _intent = New("succeeded");

        var charge = await Gateway().CaptureAsync(IntentId, "service-capture:abc");

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/v1/payment_intents/{IntentId}/capture", request.Path);
        var options = Assert.IsType<PaymentIntentCaptureOptions>(request.Options);
        // The whole block: no amount to capture, no fee, no transfer.
        Assert.Null(options.AmountToCapture);
        Assert.Null(options.ApplicationFeeAmount);
        Assert.Null(options.TransferData);
        Assert.Null(request.RequestOptions!.StripeAccount);
        Assert.Equal("service-capture:abc", request.RequestOptions.IdempotencyKey);
        Assert.Equal("succeeded", charge.Status);
        Assert.Equal(ChargeId, charge.LatestChargeId);
        Assert.Equal(6_000, charge.AmountReceivedCents);
    }

    [Fact]
    public async Task CaptureAsync_ABlockThatCannotBeChargedAnyMore_IsReturnedAsItIsNotAnError()
    {
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, $"/v1/payment_intents/{IntentId}/capture", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(
                HttpStatusCode.BadRequest,
                new StripeError { Type = "invalid_request_error", Code = "payment_intent_unexpected_state" },
                "cannot be captured"));
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Get, $"/v1/payment_intents/{IntentId}", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(New("canceled"));

        var charge = await Gateway().CaptureAsync(IntentId, "service-capture:abc");

        // The caller sees the truth (the block expired) and decides from it.
        Assert.Equal("canceled", charge.Status);
    }

    [Fact]
    public async Task CaptureAsync_ARefusalOfTheBank_ReachesTheCaller()
    {
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, $"/v1/payment_intents/{IntentId}/capture", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.PaymentRequired, new StripeError { Type = "card_error", Code = "card_declined" }, "declined"));

        await Assert.ThrowsAsync<StripeException>(() => Gateway().CaptureAsync(IntentId, "service-capture:abc"));
    }

    // ─── Get and cancel ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReadsThePaymentIntentFromThePlatform_WithItsCharge()
    {
        _intent = New("requires_capture");
        _intent.LatestCharge = new Charge
        {
            Id = ChargeId,
            PaymentMethodDetails = new ChargePaymentMethodDetails
            {
                Card = new ChargePaymentMethodDetailsCard { CaptureBefore = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc) },
            },
        };

        var charge = await Gateway().GetAsync(IntentId);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/v1/payment_intents/{IntentId}", request.Path);
        Assert.Null(request.RequestOptions?.StripeAccount);
        Assert.Contains("latest_charge", Assert.IsType<PaymentIntentGetOptions>(request.Options).Expand);
        Assert.Equal("requires_capture", charge.Status);
        Assert.Equal(6_000, charge.AmountCapturableCents);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc), charge.CaptureBefore);
    }

    [Fact]
    public async Task CancelAsync_ReleasesTheBlockOnThePlatformWithTheKey()
    {
        _intent = New("canceled");

        var charge = await Gateway().CancelAsync(IntentId, "service-charge-cancel:abc:pi_held_1");

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/v1/payment_intents/{IntentId}/cancel", request.Path);
        Assert.Null(request.RequestOptions!.StripeAccount);
        Assert.Equal("service-charge-cancel:abc:pi_held_1", request.RequestOptions.IdempotencyKey);
        Assert.Equal("canceled", charge.Status);
    }

    [Fact]
    public async Task CancelAsync_ABlockThatWasChargedMeanwhile_IsReturnedAsItIs_SoTheCallerGivesTheMoneyBack()
    {
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

        var charge = await Gateway().CancelAsync(IntentId, "key");

        Assert.Equal("succeeded", charge.Status);
    }

    [Fact]
    public async Task CancelAsync_AnyOtherStripeError_ReachesTheCaller()
    {
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, $"/v1/payment_intents/{IntentId}/cancel", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        await Assert.ThrowsAsync<StripeException>(() => Gateway().CancelAsync(IntentId, "key"));
    }

    // ─── Refund ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRefundAsync_GivesMoneyBackFromThePlatform_WithNoCommissionToReturn()
    {
        var requests = new List<(string Path, BaseOptions? Options, RequestOptions? RequestOptions)>();
        _client
            .Setup(c => c.RequestAsync<Refund>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, p, o, r, _) => requests.Add((p, o, r)))
            .ReturnsAsync(new Refund { Id = "re_1", PaymentIntentId = IntentId, Amount = 6_000, Status = "succeeded" });

        var refund = await Gateway().CreateRefundAsync(
            new PlatformRefundRequest(IntentId, 6_000, "service-charge-refund:abc:1", new Dictionary<string, string> { ["serviceRequestPaymentRefundId"] = "r1" }));

        var request = Assert.Single(requests);
        Assert.Equal("/v1/refunds", request.Path);
        var options = Assert.IsType<RefundCreateOptions>(request.Options);
        Assert.Equal(IntentId, options.PaymentIntent);
        Assert.Equal(6_000, options.Amount);
        Assert.Null(options.RefundApplicationFee);
        Assert.Equal("service-charge-refund", options.Metadata["kind"]);
        Assert.Equal("r1", options.Metadata["serviceRequestPaymentRefundId"]);
        Assert.Null(request.RequestOptions!.StripeAccount);
        Assert.Equal("service-charge-refund:abc:1", request.RequestOptions.IdempotencyKey);
        Assert.Equal("re_1", refund.Id);
        Assert.Equal("succeeded", refund.Status);
    }

    // ─── Transfer ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTransferAsync_PaysTheSupplierFromTheChargeOfThePayment_WithTheGroupAndTheKey()
    {
        var requests = new List<(string Path, BaseOptions? Options, RequestOptions? RequestOptions)>();
        _client
            .Setup(c => c.RequestAsync<Transfer>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, p, o, r, _) => requests.Add((p, o, r)))
            .ReturnsAsync(new Transfer
            {
                Id = "tr_1",
                Amount = 5_400,
                Currency = "eur",
                DestinationId = "acct_supplier",
                SourceTransactionId = ChargeId,
                TransferGroup = Group,
                Metadata = new Dictionary<string, string> { ["serviceRequestPaymentId"] = "p1" },
            });

        var transfer = await Gateway().CreateTransferAsync(
            new ServiceTransferRequest(
                5_400,
                "eur",
                "acct_supplier",
                ChargeId,
                Group,
                new Dictionary<string, string> { ["serviceRequestPaymentId"] = "p1" },
                "service-transfer:abc:0"));

        var request = Assert.Single(requests);
        Assert.Equal("/v1/transfers", request.Path);
        var options = Assert.IsType<TransferCreateOptions>(request.Options);
        Assert.Equal(5_400, options.Amount);
        Assert.Equal("eur", options.Currency);
        Assert.Equal("acct_supplier", options.Destination);
        // Tied to the money of this payment: it works while the charge is not settled, and the funds follow the charge's.
        Assert.Equal(ChargeId, options.SourceTransaction);
        Assert.Equal(Group, options.TransferGroup);
        Assert.Equal("service-transfer", options.Metadata["kind"]);
        Assert.Equal("p1", options.Metadata["serviceRequestPaymentId"]);
        Assert.Null(request.RequestOptions!.StripeAccount);
        Assert.Equal("service-transfer:abc:0", request.RequestOptions.IdempotencyKey);

        Assert.Equal("tr_1", transfer.Id);
        Assert.Equal(5_400, transfer.AmountCents);
        Assert.Equal("acct_supplier", transfer.DestinationAccountId);
        Assert.Equal(ChargeId, transfer.SourceChargeId);
        Assert.False(transfer.Reversed);
    }

    [Fact]
    public async Task CreateTransferAsync_WithoutTheChargeOrTheAccountOrTheKey_IsRefusedBeforeAnyCall()
    {
        var valid = new ServiceTransferRequest(5_400, "eur", "acct_supplier", ChargeId, Group, new Dictionary<string, string>(), "key");

        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateTransferAsync(valid with { SourceChargeId = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateTransferAsync(valid with { DestinationAccountId = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateTransferAsync(valid with { IdempotencyKey = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateTransferAsync(valid with { TransferGroup = "" }));
        _client.Verify(
            c => c.RequestAsync<Transfer>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ListTransfersAsync_FindsTheTransfersOfTheGroup()
    {
        var requests = new List<(string Path, BaseOptions? Options)>();
        _client
            .Setup(c => c.RequestAsync<StripeList<Transfer>>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, p, o, _, _) => requests.Add((p, o)))
            .ReturnsAsync(new StripeList<Transfer>
            {
                Data = [new Transfer { Id = "tr_1", Amount = 5_400, Currency = "eur", DestinationId = "acct_supplier", Reversed = true }],
                HasMore = false,
            });

        var transfers = await Gateway().ListTransfersAsync(Group);

        var request = Assert.Single(requests);
        Assert.Equal("/v1/transfers", request.Path);
        Assert.Equal(Group, Assert.IsType<TransferListOptions>(request.Options).TransferGroup);
        var transfer = Assert.Single(transfers);
        Assert.Equal("tr_1", transfer.Id);
        Assert.True(transfer.Reversed);
        Assert.Empty(transfer.Metadata);
    }

    [Fact]
    public void TheKeysAndMarkersOfTheMoneyCasaZenHolds_AreStable()
    {
        var id = Guid.Parse("3f2c1a40-7c11-4b53-8e8a-0c2f4d9a1b11");

        Assert.Equal("service-hold-3f2c1a407c114b538e8a0c2f4d9a1b11", ServiceCharges.HoldTransferGroup(id));
        Assert.Equal("service-request-3f2c1a407c114b538e8a0c2f4d9a1b11", ServiceCharges.TransferGroup(id));
        Assert.Equal("service-hold:3f2c1a407c114b538e8a0c2f4d9a1b11", ServiceCharges.HoldAuthorizationIdempotencyKey(id));
        Assert.Equal("service-capture:3f2c1a407c114b538e8a0c2f4d9a1b11", ServiceCharges.CaptureIdempotencyKey(id));
        Assert.Equal("service-transfer:3f2c1a407c114b538e8a0c2f4d9a1b11:0", ServiceCharges.TransferIdempotencyKey(id, 0));
        // A refused transfer is tried again with another key, a try whose answer was lost with the same one.
        Assert.NotEqual(ServiceCharges.TransferIdempotencyKey(id, 0), ServiceCharges.TransferIdempotencyKey(id, 1));
        Assert.True(ServiceCharges.IsCapturable("requires_capture"));
        Assert.False(ServiceCharges.IsCapturable("succeeded"));
        Assert.False(ServiceCharges.IsPayable("requires_capture"));
    }
}
