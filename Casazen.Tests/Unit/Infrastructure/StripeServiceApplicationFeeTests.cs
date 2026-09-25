using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// A3-40: <c>ApplicationFeeAmount</c> must be left unset (null) on the direct-charge PaymentIntents created on the
/// host's connected account, never sent as an explicit <c>0</c>. Stripe treats the two differently — a nullable field
/// left unset means "no application fee"; an explicit <c>0</c> is a real value on the wire that some Stripe API
/// versions have been known to reject on a direct charge, and its presence also invites the (wrong) reading later
/// that "the fee is exactly zero" versus "no fee applies". Checked on a mocked <see cref="IStripeClient"/>, same
/// pattern as <see cref="StripeServiceDeferredChargeTests"/> and <see cref="StripeServiceRefundTests"/>.
/// </summary>
public class StripeServiceApplicationFeeTests
{
    private const string Account = "acct_host_123";

    private readonly Mock<IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, BaseOptions? Options, RequestOptions? RequestOptions)> _requests = [];

    public StripeServiceApplicationFeeTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((m, p, o, r, _) => _requests.Add((m, p, o, r)))
            .ReturnsAsync(new PaymentIntent { Id = "pi_1", Status = "requires_payment_method" });
    }

    [Fact]
    public async Task CreateConnectedAccountPaymentIntentAsync_DirectCharge_LeavesApplicationFeeAmountUnset()
    {
        await Service().CreateConnectedAccountPaymentIntentAsync(
            Account, 12_345, "eur", new Dictionary<string, string> { ["bookingId"] = "b1" });

        var request = Assert.Single(_requests);
        var options = Assert.IsType<PaymentIntentCreateOptions>(request.Options);
        // Null, not 0: an explicit 0 is a real value Stripe could reject on a direct charge, and reads as "fee is
        // zero" rather than "no fee applies" (A3-40).
        Assert.Null(options.ApplicationFeeAmount);
    }

    [Fact]
    public async Task ChargePaymentMethodAsync_DeferredOffSessionCharge_LeavesApplicationFeeAmountUnset()
    {
        await Service().ChargePaymentMethodAsync(
            Account,
            "cus_1",
            "pm_1",
            12_345,
            "eur",
            new Dictionary<string, string> { ["kind"] = "direct-booking-deadline-charge" },
            "direct-booking-deadline:b1:20261001:1");

        var request = Assert.Single(_requests);
        var options = Assert.IsType<PaymentIntentCreateOptions>(request.Options);
        Assert.Null(options.ApplicationFeeAmount);
    }

    private StripeService Service() => new(NullLogger<StripeService>.Instance, _client.Object);
}
