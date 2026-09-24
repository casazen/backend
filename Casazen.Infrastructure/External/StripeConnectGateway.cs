using System.Net;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Stripe Connect calls of the onboarding. Requests go through <see cref="IStripeClient"/>: the global client configured
/// from <c>Stripe:SecretKey</c>, or the one passed in (tests answer with Stripe errors on a mocked client). Every failure
/// becomes a <see cref="StripeConnectException"/> classified by <see cref="Classify"/> (BK-09, A3-19).
/// </summary>
public class StripeConnectGateway(
    IConfiguration configuration,
    ILogger<StripeConnectGateway> logger,
    IStripeClient? stripeClient = null) : IStripeConnectGateway
{
    /// <summary>
    /// Stripe error codes of an account that does not exist (<c>resource_missing</c>) or that the platform key can no longer
    /// reach (<c>account_invalid</c>, e.g. access revoked). Values of the <c>ErrorCode</c> list generated from Stripe's
    /// OpenAPI spec in the official SDK (stripe-go <c>error.go</c>); descriptions in https://docs.stripe.com/error-codes.
    /// </summary>
    internal static readonly IReadOnlySet<string> AccountUnavailableCodes =
        new HashSet<string>(StringComparer.Ordinal) { "resource_missing", "account_invalid" };

    /// <summary>Stripe error codes of a platform key that no longer works (same source as <see cref="AccountUnavailableCodes"/>).</summary>
    internal static readonly IReadOnlySet<string> ConfigurationCodes =
        new HashSet<string>(StringComparer.Ordinal) { "api_key_expired", "platform_api_key_expired", "secret_key_required" };

    private IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

    public async Task<string> CreateExpressAccountAsync(
        string email,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        EnsureApiKey();

        var country = configuration["Stripe:ConnectDefaultCountry"] ?? "IT";

        var options = new AccountCreateOptions
        {
            Type = "express",
            Country = country,
            Capabilities = new AccountCapabilitiesOptions
            {
                CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true },
                Transfers = new AccountCapabilitiesTransfersOptions { Requested = true },
            },
        };

        if (!string.IsNullOrWhiteSpace(email))
            options.Email = email.Trim();

        var account = await CallAsync(
            "create the Express account",
            connectedAccountId: null,
            () => new AccountService(Client).CreateAsync(
                options,
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken),
            cancellationToken);

        logger.LogInformation("Stripe Express account created: {AccountId}", account.Id);
        return account.Id;
    }

    public async Task<ConnectAccountSnapshot> GetAccountAsync(
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        EnsureApiKey();

        var account = await CallAsync(
            "read the connected account",
            connectedAccountId,
            () => new AccountService(Client).GetAsync(connectedAccountId, cancellationToken: cancellationToken),
            cancellationToken);

        return MapAccount(account);
    }

    public async Task<string> CreateAccountOnboardingLinkAsync(
        string connectedAccountId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default)
    {
        EnsureApiKey();

        var options = new AccountLinkCreateOptions
        {
            Account = connectedAccountId,
            RefreshUrl = refreshUrl,
            ReturnUrl = returnUrl,
            Type = "account_onboarding",
        };

        var link = await CallAsync(
            "create the onboarding link",
            connectedAccountId,
            () => new AccountLinkService(Client).CreateAsync(options, cancellationToken: cancellationToken),
            cancellationToken);

        return link.Url;
    }

    /// <summary>
    /// How a failed Stripe call is handled (BK-09). Stripe.net 50.1 (<c>SystemNetHttpClient</c>) already retried
    /// connection errors, 409, 5xx and the 429 marked <c>Stripe-Should-Retry</c> (e.g. <c>lock_timeout</c>); what reaches
    /// here is final for this request. A network failure is not a <see cref="StripeException"/>: the SDK rethrows the
    /// <see cref="HttpRequestException"/> or the timeout's <see cref="OperationCanceledException"/>.
    /// </summary>
    internal static StripeConnectFailure Classify(Exception exception) => exception switch
    {
        StripeException { StripeError.Code: { } code } when AccountUnavailableCodes.Contains(code) =>
            StripeConnectFailure.AccountUnavailable,
        StripeException { StripeError.Code: { } code } when ConfigurationCodes.Contains(code) =>
            StripeConnectFailure.Configuration,
        StripeException { HttpStatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict } =>
            StripeConnectFailure.Transient,
        StripeException { HttpStatusCode: var status } when (int)status >= 500 => StripeConnectFailure.Transient,
        StripeException { HttpStatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            StripeConnectFailure.Configuration,
        StripeException => StripeConnectFailure.Rejected,
        HttpRequestException or OperationCanceledException => StripeConnectFailure.Transient,
        _ => StripeConnectFailure.Rejected,
    };

    /// <summary>The <see cref="StripeConnectException"/> of a failed Stripe call (see <see cref="Classify"/>).</summary>
    internal static StripeConnectException ToConnectException(Exception exception, string operation)
    {
        var failure = Classify(exception);
        var code = (exception as StripeException)?.StripeError?.Code;
        var status = exception is StripeException stripe ? (int)stripe.HttpStatusCode : (int?)null;
        return new StripeConnectException(
            failure,
            $"Stripe Connect: could not {operation} ({failure}, HTTP {status?.ToString() ?? "none"}, code {code ?? "none"}).",
            exception,
            code);
    }

    private async Task<T> CallAsync<T>(
        string operation,
        string? connectedAccountId,
        Func<Task<T>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is StripeException or HttpRequestException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var connectException = ToConnectException(ex, operation);
            var level = connectException.Failure is StripeConnectFailure.Transient or StripeConnectFailure.AccountUnavailable
                ? LogLevel.Warning
                : LogLevel.Error;
            logger.Log(
                level,
                ex,
                "Stripe Connect call failed: {Operation} for {AccountId} ({Failure}, Stripe code {StripeErrorCode})",
                operation,
                connectedAccountId ?? "(new account)",
                connectException.Failure,
                connectException.StripeErrorCode ?? "none");
            throw connectException;
        }
    }

    private void EnsureApiKey()
    {
        var secretKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new StripeConnectException(
                StripeConnectFailure.Configuration,
                "Stripe is not configured on the API server. Set Stripe__SecretKey in Railway.");
        }

        if (secretKey.Contains("...", StringComparison.Ordinal) ||
            secretKey is "sk_test_" or "sk_live_")
        {
            throw new StripeConnectException(
                StripeConnectFailure.Configuration,
                "Stripe API key is a placeholder. Configure a valid sk_test_ or sk_live_ key on the API server.");
        }

        if (stripeClient is null)
            StripeConfiguration.ApiKey = secretKey;
    }

    internal static ConnectAccountSnapshot MapAccount(Account account)
    {
        var requirements = account.Requirements?.CurrentlyDue ?? [];
        return new ConnectAccountSnapshot(
            account.Id,
            account.ChargesEnabled,
            account.PayoutsEnabled,
            account.DetailsSubmitted,
            requirements.ToList());
    }
}
