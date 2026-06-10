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

        var options = new AccountCreateOptions
        {
            Type = "express",
            Email = email,
            Capabilities = new AccountCapabilitiesOptions
            {
                CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true },
                Transfers = new AccountCapabilitiesTransfersOptions { Requested = true },
            },
        };

        var service = new AccountService();
        var account = await service.CreateAsync(options, cancellationToken: cancellationToken);
        logger.LogInformation("Stripe Express account created: {AccountId}", account.Id);
        return account.Id;
    }

    public async Task<ConnectAccountSnapshot> GetAccountAsync(
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        EnsureApiKey();

        var service = new AccountService();
        var account = await service.GetAsync(connectedAccountId, cancellationToken: cancellationToken);
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

        var service = new AccountLinkService();
        var link = await service.CreateAsync(options, cancellationToken: cancellationToken);
        return link.Url;
    }

    private void EnsureApiKey()
    {
        var secretKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Stripe:SecretKey is not configured");

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
