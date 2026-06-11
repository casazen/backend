using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.External;

public class StripeConnectGateway(IConfiguration configuration, ILogger<StripeConnectGateway> logger)
    : IStripeConnectGateway
{
    public async Task<string> CreateExpressAccountAsync(string email, CancellationToken cancellationToken = default)
    {
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

        try
        {
            var service = new AccountService();
            var account = await service.CreateAsync(options, cancellationToken: cancellationToken);
            logger.LogInformation("Stripe Express account created: {AccountId}", account.Id);
            return account.Id;
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe Connect account creation failed");
            throw new PaymentProcessingException(
                ex.StripeError?.Message ?? "Stripe Connect account creation failed. Verify platform API keys and Connect settings.",
                ex);
        }
    }

    public async Task<ConnectAccountSnapshot> GetAccountAsync(
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        EnsureApiKey();

        try
        {
            var service = new AccountService();
            var account = await service.GetAsync(connectedAccountId, cancellationToken: cancellationToken);
            return MapAccount(account);
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe Connect account lookup failed for {AccountId}", connectedAccountId);
            throw new PaymentProcessingException(
                ex.StripeError?.Message ?? "Unable to load Stripe Connect account status.",
                ex);
        }
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

        try
        {
            var service = new AccountLinkService();
            var link = await service.CreateAsync(options, cancellationToken: cancellationToken);
            return link.Url;
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe Connect onboarding link creation failed for {AccountId}", connectedAccountId);
            throw new PaymentProcessingException(
                ex.StripeError?.Message ?? "Unable to create Stripe onboarding link.",
                ex);
        }
    }

    private void EnsureApiKey()
    {
        var secretKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new PaymentProcessingException(
                "Stripe is not configured on the API server. Set Stripe__SecretKey in Railway.");
        }

        if (secretKey.Contains("...", StringComparison.Ordinal) ||
            secretKey is "sk_test_" or "sk_live_")
        {
            throw new PaymentProcessingException(
                "Stripe API key is a placeholder. Configure a valid sk_test_ or sk_live_ key on the API server.");
        }

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
