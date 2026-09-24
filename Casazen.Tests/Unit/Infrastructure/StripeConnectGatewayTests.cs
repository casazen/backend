using System.Net;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// BK-09 (A3-19): the Stripe Connect calls on a mocked <see cref="IStripeClient"/> that answers like Stripe. Only an
/// account that does not exist or was revoked is <see cref="StripeConnectFailure.AccountUnavailable"/>; rate limits,
/// outages and network errors are transient, key problems are configuration errors. The account creation carries the
/// idempotency key it is given.
/// </summary>
public class StripeConnectGatewayTests
{
    private const string AccountId = "acct_verified_host";

    private readonly Mock<IStripeClient> _client = new();

    public StripeConnectGatewayTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
    }

    public static TheoryData<HttpStatusCode, string?, StripeConnectFailure> StripeErrors => new()
    {
        // Account gone or no longer reachable by the platform key: the only case that may unlink.
        { HttpStatusCode.NotFound, "resource_missing", StripeConnectFailure.AccountUnavailable },
        { HttpStatusCode.Forbidden, "account_invalid", StripeConnectFailure.AccountUnavailable },
        // Temporary.
        { HttpStatusCode.TooManyRequests, "rate_limit", StripeConnectFailure.Transient },
        { HttpStatusCode.TooManyRequests, "lock_timeout", StripeConnectFailure.Transient },
        { HttpStatusCode.Conflict, null, StripeConnectFailure.Transient },
        { HttpStatusCode.InternalServerError, null, StripeConnectFailure.Transient },
        { HttpStatusCode.BadGateway, null, StripeConnectFailure.Transient },
        { HttpStatusCode.ServiceUnavailable, null, StripeConnectFailure.Transient },
        // Platform key.
        { HttpStatusCode.Unauthorized, null, StripeConnectFailure.Configuration },
        { HttpStatusCode.Unauthorized, "api_key_expired", StripeConnectFailure.Configuration },
        { HttpStatusCode.Forbidden, null, StripeConnectFailure.Configuration },
        // Anything else Stripe refuses.
        { HttpStatusCode.BadRequest, "parameter_invalid_empty", StripeConnectFailure.Rejected },
        { HttpStatusCode.BadRequest, null, StripeConnectFailure.Rejected },
    };

    [Theory]
    [MemberData(nameof(StripeErrors))]
    public async Task GetAccountAsync_StripeError_ThrowsClassifiedConnectException(
        HttpStatusCode status,
        string? code,
        StripeConnectFailure expected)
    {
        SetupGetAccount().ThrowsAsync(StripeError(status, code));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway().GetAccountAsync(AccountId));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal(code, ex.StripeErrorCode);
        Assert.IsType<StripeException>(ex.InnerException);
    }

    [Fact]
    public async Task GetAccountAsync_RateLimited429_IsTransientNotAccountUnavailable()
    {
        SetupGetAccount().ThrowsAsync(StripeError(HttpStatusCode.TooManyRequests, "rate_limit"));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway().GetAccountAsync(AccountId));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
        // Still a PaymentProcessingException: a caller that does not handle it answers 503, never 500.
        Assert.IsAssignableFrom<PaymentProcessingException>(ex);
    }

    [Fact]
    public async Task GetAccountAsync_NetworkError_IsTransient()
    {
        // Stripe.net rethrows the HttpRequestException after its own retries (not a StripeException).
        SetupGetAccount().ThrowsAsync(new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway().GetAccountAsync(AccountId));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
    }

    [Fact]
    public async Task GetAccountAsync_HttpTimeout_IsTransient()
    {
        SetupGetAccount().ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway().GetAccountAsync(AccountId));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
    }

    [Fact]
    public async Task GetAccountAsync_CallerCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        SetupGetAccount().ThrowsAsync(new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => Gateway().GetAccountAsync(AccountId, cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk_test_...")]
    [InlineData("sk_live_")]
    public async Task GetAccountAsync_MissingOrPlaceholderKey_ThrowsConfigurationWithoutCallingStripe(string? secretKey)
    {
        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway(secretKey).GetAccountAsync(AccountId));

        Assert.Equal(StripeConnectFailure.Configuration, ex.Failure);
        _client.Verify(
            c => c.RequestAsync<Account>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAccountAsync_Success_MapsCapabilities()
    {
        SetupGetAccount().ReturnsAsync(new Account
        {
            Id = AccountId,
            ChargesEnabled = true,
            PayoutsEnabled = true,
            DetailsSubmitted = true,
            Requirements = new AccountRequirements { CurrentlyDue = ["external_account"] },
        });

        var snapshot = await Gateway().GetAccountAsync(AccountId);

        Assert.Equal(AccountId, snapshot.AccountId);
        Assert.True(snapshot.ChargesEnabled);
        Assert.Equal(new[] { "external_account" }, snapshot.RequirementsDue);
    }

    [Fact]
    public async Task CreateExpressAccountAsync_SendsIdempotencyKeyAndExpressOptions()
    {
        RequestOptions? sentRequestOptions = null;
        BaseOptions? sentOptions = null;
        _client
            .Setup(c => c.RequestAsync<Account>(
                HttpMethod.Post, "/v1/accounts", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, o, r, _) =>
            {
                sentOptions = o;
                sentRequestOptions = r;
            })
            .ReturnsAsync(new Account { Id = "acct_new" });

        var accountId = await Gateway().CreateExpressAccountAsync(" host@example.com ", "connect-account:abc");

        Assert.Equal("acct_new", accountId);
        Assert.Equal("connect-account:abc", sentRequestOptions?.IdempotencyKey);
        var options = Assert.IsType<AccountCreateOptions>(sentOptions);
        Assert.Equal("express", options.Type);
        Assert.Equal("IT", options.Country);
        Assert.Equal("host@example.com", options.Email);
    }

    [Fact]
    public async Task CreateAccountOnboardingLinkAsync_SendsServerUrls()
    {
        BaseOptions? sentOptions = null;
        _client
            .Setup(c => c.RequestAsync<AccountLink>(
                HttpMethod.Post, "/v1/account_links", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, o, _, _) => sentOptions = o)
            .ReturnsAsync(new AccountLink { Url = "https://connect.stripe.com/setup/e/acct/abc" });

        var url = await Gateway().CreateAccountOnboardingLinkAsync(
            AccountId,
            "https://app.example.org/app/short-rent/settings/payments?stripe_return=1",
            "https://app.example.org/app/short-rent/settings/payments?stripe_refresh=1");

        Assert.Equal("https://connect.stripe.com/setup/e/acct/abc", url);
        var options = Assert.IsType<AccountLinkCreateOptions>(sentOptions);
        Assert.Equal(AccountId, options.Account);
        Assert.Equal("account_onboarding", options.Type);
        Assert.Equal("https://app.example.org/app/short-rent/settings/payments?stripe_return=1", options.ReturnUrl);
        Assert.Equal("https://app.example.org/app/short-rent/settings/payments?stripe_refresh=1", options.RefreshUrl);
    }

    /// <summary>A Stripe error as Stripe.net 50.1 builds it from the JSON body (<c>LiveApiRequestor.BuildStripeException</c>).</summary>
    internal static StripeException StripeError(HttpStatusCode status, string? code) =>
        new(status, new StripeError { Type = "invalid_request_error", Code = code, Message = $"Stripe error {code}" }, $"Stripe error {code}");

    private Moq.Language.Flow.ISetup<IStripeClient, Task<Account>> SetupGetAccount() =>
        _client.Setup(c => c.RequestAsync<Account>(
            HttpMethod.Get, $"/v1/accounts/{AccountId}", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()));

    private StripeConnectGateway Gateway(string? secretKey = "sk_test_unit_connect") =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:SecretKey"] = secretKey })
                .Build(),
            NullLogger<StripeConnectGateway>.Instance,
            _client.Object);
}
