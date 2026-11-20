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

        var accountId = await Gateway().CreateExpressAccountAsync(" host@example.com ", "IT", "connect-account:abc");

        Assert.Equal("acct_new", accountId);
        Assert.Equal("connect-account:abc", sentRequestOptions?.IdempotencyKey);
        var options = Assert.IsType<AccountCreateOptions>(sentOptions);
        Assert.Equal("express", options.Type);
        Assert.True(options.Capabilities.SepaDebitPayments.Requested);
        Assert.Equal("IT", options.Country);
        Assert.Equal("host@example.com", options.Email);
    }

    [Theory]
    [InlineData("CH", "CH")]
    [InlineData(" ch ", "CH")]
    [InlineData("IT", "IT")]
    public async Task CreateExpressAccountAsync_CreatesTheAccountInTheGivenCountry_NotAFixedOne(string country, string expected)
    {
        // Issue #1001: the country was always the platform default, so a Swiss company got an Italian form.
        var created = SetupCreateAccount();

        await Gateway().CreateExpressAccountAsync("host@example.com", country, "connect-account:abc");

        Assert.Equal(expected, Assert.IsType<AccountCreateOptions>(created.Options).Country);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateExpressAccountAsync_BlankCountry_ThrowsBeforeCallingStripe(string country)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateExpressAccountAsync("host@example.com", country, "connect-account:abc"));

        _client.Verify(
            c => c.RequestAsync<Account>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("IT", "Milano", "MI", "20121", "+390000000000", "eur", "IT60X0542811101000000123456", "000000000")]
    [InlineData("CH", "Lugano", "TI", "6900", "+410000000000", "chf", "CH9300762011623852957", null)]
    public async Task CreateExpressAccountAsync_TestModeKey_FillsTheNewAccountWithStripeTestData(
        string country,
        string city,
        string state,
        string postalCode,
        string phone,
        string currency,
        string iban,
        string? idNumber)
    {
        // Issue #1001: in test mode Stripe verifies identities against nothing but its published test values; a real name,
        // birth date or address fails with verification_failed_keyed_identity and the account never leaves the onboarding.
        var created = SetupCreateAccount();
        var update = SetupUpdateAccount();

        var accountId = await Gateway().CreateExpressAccountAsync("host@example.com", country, "connect-account:abc");

        Assert.Equal("acct_new", accountId);
        var options = Assert.IsType<AccountUpdateOptions>(update.Options);
        Assert.Equal("individual", options.BusinessType);
        Assert.Equal("6513", options.BusinessProfile.Mcc);
        Assert.False(string.IsNullOrWhiteSpace(options.BusinessProfile.ProductDescription));
        // Stripe refuses reserved domains in test mode too, which would fail the whole update: no URL is sent.
        Assert.Null(options.BusinessProfile.Url);
        var individual = options.Individual;
        Assert.Equal("host@example.com", individual.Email);
        Assert.Equal(phone, individual.Phone);
        Assert.Equal(1, individual.Dob.Day);
        Assert.Equal(1, individual.Dob.Month);
        Assert.Equal(1901, individual.Dob.Year);
        Assert.Equal("address_full_match", individual.Address.Line1);
        Assert.Equal(city, individual.Address.City);
        Assert.Equal(state, individual.Address.State);
        Assert.Equal(postalCode, individual.Address.PostalCode);
        Assert.Equal(country, individual.Address.Country);
        Assert.Equal(idNumber, individual.IdNumber);
        Assert.Equal("bank_account", options.ExtraParams["external_account[object]"]);
        Assert.Equal(country, options.ExtraParams["external_account[country]"]);
        Assert.Equal(currency, options.ExtraParams["external_account[currency]"]);
        Assert.Equal(iban, options.ExtraParams["external_account[account_number]"]);
        // The creation itself is unchanged by the prefill and still carries the idempotency key.
        Assert.Equal("connect-account:abc", created.RequestOptions?.IdempotencyKey);
        Assert.Null(Assert.IsType<AccountCreateOptions>(created.Options).Individual);
    }

    [Theory]
    [InlineData("sk_live_unit_connect", null)]
    [InlineData("rk_live_unit_connect", null)]
    [InlineData("sk_test_unit_connect", "false")]
    [InlineData("rk_test_unit_connect", "False")]
    public async Task CreateExpressAccountAsync_LiveKeyOrPrefillSwitchedOff_NeverFillsTheAccount(string secretKey, string? prefillSetting)
    {
        // A live account is never written with fake identities; the test environment can also ask for the real hosted form.
        SetupCreateAccount();
        var update = SetupUpdateAccount();

        var accountId = await Gateway(secretKey, prefillSetting).CreateExpressAccountAsync("host@example.com", "IT", "connect-account:abc");

        Assert.Equal("acct_new", accountId);
        Assert.Null(update.Options);
        _client.Verify(
            c => c.RequestAsync<Account>(
                HttpMethod.Post, "/v1/accounts/acct_new", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateExpressAccountAsync_CountryWithoutTestData_KeepsTheEmptyHostedOnboarding()
    {
        SetupCreateAccount();
        var update = SetupUpdateAccount();

        var accountId = await Gateway().CreateExpressAccountAsync("host@example.com", "DE", "connect-account:abc");

        Assert.Equal("acct_new", accountId);
        Assert.Null(update.Options);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "url_invalid")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit")]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    public async Task CreateExpressAccountAsync_TestDataRejectedByStripe_StillReturnsTheCreatedAccount(HttpStatusCode status, string? code)
    {
        // The account exists and is linked by the caller: the prefill is a help, never a reason to lose it.
        SetupCreateAccount();
        _client
            .Setup(c => c.RequestAsync<Account>(
                HttpMethod.Post, "/v1/accounts/acct_new", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeError(status, code));

        var accountId = await Gateway().CreateExpressAccountAsync("host@example.com", "IT", "connect-account:abc");

        Assert.Equal("acct_new", accountId);
    }

    [Fact]
    public async Task CreateExpressAccountAsync_TestDataNetworkError_StillReturnsTheCreatedAccount()
    {
        SetupCreateAccount();
        _client
            .Setup(c => c.RequestAsync<Account>(
                HttpMethod.Post, "/v1/accounts/acct_new", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var accountId = await Gateway().CreateExpressAccountAsync("host@example.com", "CH", "connect-account:abc");

        Assert.Equal("acct_new", accountId);
    }

    [Fact]
    public async Task CreateExpressAccountAsync_CallerCancelsDuringTheTestData_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        SetupCreateAccount();
        _client
            .Setup(c => c.RequestAsync<Account>(
                HttpMethod.Post, "/v1/accounts/acct_new", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Gateway().CreateExpressAccountAsync("host@example.com", "IT", "connect-account:abc", cts.Token));
    }

    [Fact]
    public async Task GetAccountAsync_Success_MapsTheCountryOfTheAccount()
    {
        SetupGetAccount().ReturnsAsync(new Account { Id = AccountId, Country = "CH" });

        var snapshot = await Gateway().GetAccountAsync(AccountId);

        Assert.Equal("CH", snapshot.Country);
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

    [Fact]
    public async Task CreateDashboardLoginLinkAsync_PostsToTheLoginLinksOfTheAccount_ReturnsTheUrl()
    {
        // SP-14: POST /v1/accounts/{id}/login_links, the single-use link to the Express Dashboard of the supplier.
        SetupLoginLink().ReturnsAsync(new LoginLink { Url = "https://connect.stripe.com/express/abc" });

        var url = await Gateway().CreateDashboardLoginLinkAsync(AccountId);

        Assert.Equal("https://connect.stripe.com/express/abc", url);
        _client.Verify(
            c => c.RequestAsync<LoginLink>(
                HttpMethod.Post, $"/v1/accounts/{AccountId}/login_links", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [MemberData(nameof(StripeErrors))]
    public async Task CreateDashboardLoginLinkAsync_StripeError_ThrowsClassifiedConnectException(
        HttpStatusCode status,
        string? code,
        StripeConnectFailure expected)
    {
        SetupLoginLink().ThrowsAsync(StripeError(status, code));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway().CreateDashboardLoginLinkAsync(AccountId));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal(code, ex.StripeErrorCode);
    }

    [Fact]
    public async Task CreateDashboardLoginLinkAsync_NetworkError_IsTransient()
    {
        SetupLoginLink().ThrowsAsync(new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway().CreateDashboardLoginLinkAsync(AccountId));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk_test_...")]
    public async Task CreateDashboardLoginLinkAsync_MissingOrPlaceholderKey_ThrowsConfigurationWithoutCallingStripe(string? secretKey)
    {
        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Gateway(secretKey).CreateDashboardLoginLinkAsync(AccountId));

        Assert.Equal(StripeConnectFailure.Configuration, ex.Failure);
        _client.Verify(
            c => c.RequestAsync<LoginLink>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateDashboardLoginLinkAsync_BlankAccount_ThrowsBeforeCallingStripe(string accountId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Gateway().CreateDashboardLoginLinkAsync(accountId));

        _client.Verify(
            c => c.RequestAsync<LoginLink>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A Stripe error as Stripe.net 50.1 builds it from the JSON body (<c>LiveApiRequestor.BuildStripeException</c>).</summary>
    internal static StripeException StripeError(HttpStatusCode status, string? code) =>
        new(status, new StripeError { Type = "invalid_request_error", Code = code, Message = $"Stripe error {code}" }, $"Stripe error {code}");

    private Moq.Language.Flow.ISetup<IStripeClient, Task<Account>> SetupGetAccount() =>
        _client.Setup(c => c.RequestAsync<Account>(
            HttpMethod.Get, $"/v1/accounts/{AccountId}", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()));

    private Moq.Language.Flow.ISetup<IStripeClient, Task<LoginLink>> SetupLoginLink() =>
        _client.Setup(c => c.RequestAsync<LoginLink>(
            HttpMethod.Post, $"/v1/accounts/{AccountId}/login_links", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()));

    /// <summary>What the mocked Stripe client received for one request.</summary>
    private sealed class Sent
    {
        public BaseOptions? Options { get; set; }

        public RequestOptions? RequestOptions { get; set; }
    }

    /// <summary><c>POST /v1/accounts</c> creates <c>acct_new</c>.</summary>
    private Sent SetupCreateAccount()
    {
        var sent = new Sent();
        _client
            .Setup(c => c.RequestAsync<Account>(
                HttpMethod.Post, "/v1/accounts", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, request, _) =>
            {
                sent.Options = options;
                sent.RequestOptions = request;
            })
            .ReturnsAsync(new Account { Id = "acct_new" });
        return sent;
    }

    /// <summary><c>POST /v1/accounts/acct_new</c>, the update that fills a test account.</summary>
    private Sent SetupUpdateAccount()
    {
        var sent = new Sent();
        _client
            .Setup(c => c.RequestAsync<Account>(
                HttpMethod.Post, "/v1/accounts/acct_new", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, request, _) =>
            {
                sent.Options = options;
                sent.RequestOptions = request;
            })
            .ReturnsAsync(new Account { Id = "acct_new" });
        return sent;
    }

    private StripeConnectGateway Gateway(string? secretKey = "sk_test_unit_connect", string? prefillSetting = null) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Stripe:SecretKey"] = secretKey,
                    ["Stripe:ConnectTestPrefill"] = prefillSetting,
                })
                .Build(),
            NullLogger<StripeConnectGateway>.Instance,
            _client.Object);
}
