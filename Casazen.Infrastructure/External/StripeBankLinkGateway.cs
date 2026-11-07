using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.FinancialConnections;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Financial Connections and the PaymentIntent debit of a linked account, on the platform Stripe client already used
/// for billing. SEPA Direct Debit needs an IBAN, which is not stored. A Financial Connections account does not offer
/// a SEPA credit transfer. The debit this gateway starts is <c>us_bank_account</c> when the linked account lists it.
/// </summary>
public interface IStripeBankLinkGateway
{
    Task<string> EnsureCustomerAsync(
        string? existingCustomerId,
        string idempotencyKey,
        IReadOnlyDictionary<string, string> metadata,
        string name,
        CancellationToken cancellationToken = default);

    Task<BankLinkSession> CreateSessionAsync(string customerId, CancellationToken cancellationToken = default);

    Task<LinkedFinancialAccount> GetAccountAsync(string accountId, CancellationToken cancellationToken = default);

    Task<StartedBankDebit> StartDebitAsync(BankDebitCommand command, CancellationToken cancellationToken = default);
}

/// <summary>A Financial Connections account read back from Stripe. Account numbers are not copied.</summary>
public sealed record LinkedFinancialAccount(
    string Id,
    string? CustomerId,
    string? InstitutionName,
    string? Last4,
    string? DisplayName,
    string? Status,
    IReadOnlyList<string> SupportedPaymentMethodTypes);

/// <summary>What is needed to debit the linked account.</summary>
public sealed record BankDebitCommand(
    string CustomerId,
    string FinancialConnectionsAccountId,
    IReadOnlyList<string> SupportedPaymentMethodTypes,
    long AmountCents,
    string Currency,
    string IdempotencyKey,
    string BillingName,
    string? IpAddress,
    string? UserAgent,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>The PaymentIntent Stripe created. <see cref="Status"/> is Stripe's, not a settled due.</summary>
public sealed record StartedBankDebit(
    string PaymentIntentId,
    string? ClientSecret,
    string Status,
    string PaymentMethodType,
    string PaymentMethodId);

public sealed class StripeBankLinkGateway(
    IConfiguration configuration,
    ILogger<StripeBankLinkGateway> logger,
    IStripeClient? stripeClient = null) : IStripeBankLinkGateway
{
    private const string PaymentMethodPermission = "payment_method";

    private IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

    public async Task<string> EnsureCustomerAsync(
        string? existingCustomerId,
        string idempotencyKey,
        IReadOnlyDictionary<string, string> metadata,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(existingCustomerId))
            return existingCustomerId;

        EnsureApiKey();
        try
        {
            var customer = await new CustomerService(Client).CreateAsync(
                new CustomerCreateOptions
                {
                    Name = Trim(name, 200),
                    Metadata = new Dictionary<string, string>(metadata),
                },
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken);
            logger.LogInformation("Bank-link Stripe customer created: {CustomerId}", customer.Id);
            return customer.Id;
        }
        catch (StripeException ex)
        {
            throw ProviderFailure(ex);
        }
    }

    public async Task<BankLinkSession> CreateSessionAsync(string customerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureApiKey();
        try
        {
            var session = await new SessionService(Client).CreateAsync(
                new SessionCreateOptions
                {
                    AccountHolder = new SessionAccountHolderOptions { Type = "customer", Customer = customerId },
                    Permissions = [PaymentMethodPermission],
                },
                cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.ClientSecret))
                throw new PaymentProcessingException("Stripe Financial Connections session did not return a client secret.");

            logger.LogInformation("Financial Connections session created: {SessionId}", session.Id);
            return new BankLinkSession(session.Id, session.ClientSecret);
        }
        catch (StripeException ex)
        {
            throw ProviderFailure(ex);
        }
    }

    public async Task<LinkedFinancialAccount> GetAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (!IsAccountId(accountId))
            throw new DomainRuleException(BankLinkErrorCodes.AccountIdInvalid, "BankLinkAccountIdInvalid");

        EnsureApiKey();
        try
        {
            var account = await new Stripe.FinancialConnections.AccountService(Client).GetAsync(accountId, cancellationToken: cancellationToken);
            return new LinkedFinancialAccount(
                account.Id,
                account.AccountHolder?.CustomerId,
                account.InstitutionName,
                account.Last4,
                account.DisplayName,
                account.Status,
                account.SupportedPaymentMethodTypes ?? []);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            throw new DomainRuleException(BankLinkErrorCodes.AccountIdInvalid, "BankLinkAccountIdInvalid");
        }
        catch (StripeException ex)
        {
            throw ProviderFailure(ex);
        }
    }

    public async Task<StartedBankDebit> StartDebitAsync(BankDebitCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureApiKey();
        if (!CanDebit(command.SupportedPaymentMethodTypes, command.Currency))
            throw new DomainRuleException(BankLinkErrorCodes.DebitUnavailable, "BankLinkDebitUnavailable");

        try
        {
            var paymentMethod = await new PaymentMethodService(Client).CreateAsync(
                new PaymentMethodCreateOptions
                {
                    Type = BankLinkCharges.AchDebit,
                    Customer = command.CustomerId,
                    BillingDetails = new PaymentMethodBillingDetailsOptions { Name = Trim(command.BillingName, 200) },
                    UsBankAccount = new PaymentMethodUsBankAccountOptions
                    {
                        FinancialConnectionsAccount = command.FinancialConnectionsAccountId,
                    },
                },
                new RequestOptions { IdempotencyKey = $"{command.IdempotencyKey}:pm" },
                cancellationToken);

            var intent = await new PaymentIntentService(Client).CreateAsync(
                new PaymentIntentCreateOptions
                {
                    Amount = command.AmountCents,
                    Currency = command.Currency,
                    Customer = command.CustomerId,
                    PaymentMethod = paymentMethod.Id,
                    PaymentMethodTypes = [BankLinkCharges.AchDebit],
                    Confirm = true,
                    MandateData = new PaymentIntentMandateDataOptions
                    {
                        CustomerAcceptance = new PaymentIntentMandateDataCustomerAcceptanceOptions
                        {
                            Type = "online",
                            Online = new PaymentIntentMandateDataCustomerAcceptanceOnlineOptions
                            {
                                IpAddress = string.IsNullOrWhiteSpace(command.IpAddress) ? "127.0.0.1" : command.IpAddress,
                                UserAgent = string.IsNullOrWhiteSpace(command.UserAgent) ? "CasaZen" : Trim(command.UserAgent, 255),
                            },
                        },
                    },
                    Metadata = new Dictionary<string, string>(command.Metadata),
                },
                new RequestOptions { IdempotencyKey = $"{command.IdempotencyKey}:pi" },
                cancellationToken);

            if (intent.Status is "canceled" or "requires_payment_method")
                throw new DomainRuleException(BankLinkErrorCodes.DebitUnavailable, "BankLinkDebitUnavailable");

            logger.LogInformation(
                "Bank-link payment intent {PaymentIntentId} created ({Status}, {PaymentMethodType})",
                intent.Id,
                intent.Status,
                BankLinkCharges.AchDebit);
            return new StartedBankDebit(intent.Id, intent.ClientSecret, intent.Status, BankLinkCharges.AchDebit, paymentMethod.Id);
        }
        catch (StripeException ex)
        {
            throw ProviderFailure(ex);
        }
    }

    /// <summary>
    /// True only for an ACH debit of a linked account that lists <c>us_bank_account</c>, in USD. EUR (including a SEPA
    /// credit transfer) is not a method this account can start without an IBAN.
    /// </summary>
    internal static bool CanDebit(IReadOnlyList<string>? supportedTypes, string? currency) =>
        string.Equals(currency, BankLinkCharges.AchCurrency, StringComparison.Ordinal)
        && supportedTypes is not null
        && supportedTypes.Any(type => string.Equals(type, BankLinkCharges.AchDebit, StringComparison.Ordinal));

    internal static bool IsAccountId(string? accountId) =>
        !string.IsNullOrWhiteSpace(accountId)
        && accountId.Length <= 255
        && accountId.StartsWith("fca_", StringComparison.Ordinal)
        && accountId.Skip(4).All(char.IsAsciiLetterOrDigit);

    private void EnsureApiKey()
    {
        if (stripeClient is not null)
            return;

        var secretKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey)
            || secretKey.Contains("...", StringComparison.Ordinal)
            || secretKey.Contains("YOUR", StringComparison.Ordinal)
            || secretKey is "sk_test_" or "sk_live_")
        {
            throw new PaymentProcessingException("Stripe is not configured for Financial Connections.");
        }

        StripeConfiguration.ApiKey = secretKey;
    }

    private static PaymentProcessingException ProviderFailure(StripeException ex)
    {
        var code = ex.StripeError?.Code ?? ex.HttpStatusCode.ToString();
        return new PaymentProcessingException($"Stripe bank-link call failed ({code}).", ex);
    }

    private static string Trim(string? value, int max)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "CasaZen" : value.Trim();
        return text.Length <= max ? text : text[..max];
    }
}
