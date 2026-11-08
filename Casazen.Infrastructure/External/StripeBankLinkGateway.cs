using System.Reflection;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.FinancialConnections;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Financial Connections and the SEPA debit of a linked account, on the platform Stripe client already used for billing.
/// The session offers Italian banks. The PaymentIntent is <c>sepa_debit</c> in EUR. A full IBAN is never stored or logged:
/// the PaymentMethod is the one Stripe already attached, or it is created from the IBAN token on the account.
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

    /// <summary>Creates the SEPA PaymentIntent without confirming it. The row is saved before <see cref="ConfirmDebitAsync"/>.</summary>
    Task<StartedBankDebit> CreateDebitAsync(BankDebitCommand command, CancellationToken cancellationToken = default);

    /// <summary>Confirms a PaymentIntent that already has a <c>BankLinkPayments</c> row. The idempotency key is the due's.</summary>
    Task<StartedBankDebit> ConfirmDebitAsync(
        string paymentIntentId,
        string idempotencyKey,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A Financial Connections account read back from Stripe. <see cref="IbanToken"/> is set only when Stripe returned a
/// token, never a full IBAN. A raw IBAN stays inside the gateway for the Stripe call and is not on this record.
/// </summary>
public sealed record LinkedFinancialAccount(
    string Id,
    string? CustomerId,
    string? InstitutionName,
    string? Last4,
    string? DisplayName,
    string? Status,
    IReadOnlyList<string> SupportedPaymentMethodTypes,
    string? SepaPaymentMethodId = null,
    string? IbanToken = null);

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
            var options = new SessionCreateOptions
            {
                AccountHolder = new SessionAccountHolderOptions { Type = "customer", Customer = customerId },
                Permissions = [PaymentMethodPermission],
                Filters = new SessionFiltersOptions
                {
                    AccountSubcategories = ["checking", "savings"],
                },
            };
            // Stripe.net 50 sends Stripe-Version 2025-12-15.clover. That version only accepts filters[countries]
            // and rejects filters[country], which the API was turning into 503. filters[country] exists from
            // 2026-09-30.endive. This call pins that later version and sends Italy as the single country.
            options.AddExtraParam("filters[country]", BankLinkCharges.InstitutionCountry);
            var requestOptions = new RequestOptions();
            PinSessionApiVersion(requestOptions);
            var session = await new SessionService(Client).CreateAsync(options, requestOptions, cancellationToken);
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
            return MapAccount(account);
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

    public async Task<StartedBankDebit> CreateDebitAsync(BankDebitCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.Currency, BankLinkCharges.Currency, StringComparison.Ordinal))
            throw new DomainRuleException(BankLinkErrorCodes.CurrencyInvalid, "BankLinkCurrencyInvalid");

        EnsureApiKey();
        try
        {
            var account = await new Stripe.FinancialConnections.AccountService(Client).GetAsync(
                command.FinancialConnectionsAccountId, cancellationToken: cancellationToken);
            var instrument = ReadInstrument(account);
            var paymentMethodId = instrument.PaymentMethodId;
            if (paymentMethodId is null)
            {
                var options = new PaymentMethodCreateOptions
                {
                    Type = BankLinkCharges.SepaDebit,
                    Customer = command.CustomerId,
                    BillingDetails = new PaymentMethodBillingDetailsOptions { Name = Trim(command.BillingName, 200) },
                    SepaDebit = new PaymentMethodSepaDebitOptions(),
                };
                if (!string.IsNullOrWhiteSpace(instrument.IbanOrToken))
                    options.SepaDebit.Iban = instrument.IbanOrToken;
                else
                    options.AddExtraParam("sepa_debit[financial_connections_account]", command.FinancialConnectionsAccountId);

                var created = await new PaymentMethodService(Client).CreateAsync(
                    options,
                    new RequestOptions { IdempotencyKey = $"{command.IdempotencyKey}:pm" },
                    cancellationToken);
                paymentMethodId = created.Id;
            }

            var intent = await new PaymentIntentService(Client).CreateAsync(
                new PaymentIntentCreateOptions
                {
                    Amount = command.AmountCents,
                    Currency = BankLinkCharges.Currency,
                    Customer = command.CustomerId,
                    PaymentMethod = paymentMethodId,
                    PaymentMethodTypes = [BankLinkCharges.SepaDebit],
                    Confirm = false,
                    Metadata = new Dictionary<string, string>(command.Metadata),
                },
                new RequestOptions { IdempotencyKey = $"{command.IdempotencyKey}:pi" },
                cancellationToken);

            logger.LogInformation(
                "Bank-link payment intent {PaymentIntentId} created unconfirmed ({PaymentMethodType})",
                intent.Id,
                BankLinkCharges.SepaDebit);
            return new StartedBankDebit(intent.Id, intent.ClientSecret, intent.Status, BankLinkCharges.SepaDebit, paymentMethodId);
        }
        catch (StripeException ex)
        {
            // The Stripe message can echo an IBAN. Keep the code only.
            throw ProviderFailure(ex);
        }
    }

    public async Task<StartedBankDebit> ConfirmDebitAsync(
        string paymentIntentId,
        string idempotencyKey,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        EnsureApiKey();
        try
        {
            var intent = await new PaymentIntentService(Client).ConfirmAsync(
                paymentIntentId,
                new PaymentIntentConfirmOptions
                {
                    MandateData = new PaymentIntentMandateDataOptions
                    {
                        CustomerAcceptance = new PaymentIntentMandateDataCustomerAcceptanceOptions
                        {
                            Type = "online",
                            Online = new PaymentIntentMandateDataCustomerAcceptanceOnlineOptions
                            {
                                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? "127.0.0.1" : ipAddress,
                                UserAgent = string.IsNullOrWhiteSpace(userAgent) ? "CasaZen" : Trim(userAgent, 255),
                            },
                        },
                    },
                },
                new RequestOptions { IdempotencyKey = $"{idempotencyKey}:confirm" },
                cancellationToken);

            if (intent.Status is "canceled" or "requires_payment_method")
                throw new DomainRuleException(BankLinkErrorCodes.DebitUnavailable, "BankLinkDebitUnavailable");

            logger.LogInformation(
                "Bank-link payment intent {PaymentIntentId} confirmed ({Status})",
                intent.Id,
                intent.Status);
            return new StartedBankDebit(
                intent.Id,
                intent.ClientSecret,
                intent.Status,
                BankLinkCharges.SepaDebit,
                intent.PaymentMethodId ?? string.Empty);
        }
        catch (StripeException ex)
        {
            throw ProviderFailure(ex);
        }
    }

    internal static LinkedFinancialAccount MapAccount(Stripe.FinancialConnections.Account account)
    {
        var instrument = ReadInstrument(account);
        var token = instrument.PaymentMethodId is null && !IsFullIban(instrument.IbanOrToken) ? instrument.IbanOrToken : null;
        return new LinkedFinancialAccount(
            account.Id,
            account.AccountHolder?.CustomerId,
            account.InstitutionName,
            account.Last4,
            account.DisplayName,
            account.Status,
            account.SupportedPaymentMethodTypes ?? [],
            instrument.PaymentMethodId,
            token);
    }

    /// <summary>
    /// A SEPA payment method id (<c>pm_…</c>) or the account-number token Stripe returned. A full IBAN is included only
    /// so the PaymentMethod can be created; callers that keep the result must drop it.
    /// </summary>
    internal readonly record struct SepaInstrument(string? PaymentMethodId, string? IbanOrToken);

    internal static SepaInstrument ReadInstrument(Stripe.FinancialConnections.Account account)
    {
        var paymentMethod = account.RawJObject?["payment_method"]?.ToString();
        if (paymentMethod is not null && paymentMethod.StartsWith("pm_", StringComparison.Ordinal))
            return new SepaInstrument(paymentMethod, null);

        if (account.AccountNumbers is null)
            return default;

        foreach (var number in account.AccountNumbers)
        {
            if (number.Status is not null && !string.Equals(number.Status, "transactable", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = FirstValue(number.RawJObject, "token", "iban_token", "tokenized_account_number", "iban", "account_number");
            if (!string.IsNullOrWhiteSpace(value))
                return new SepaInstrument(null, value);
        }

        return default;
    }

    internal static bool IsFullIban(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var compact = value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return compact.Length is >= 15 and <= 34
            && char.IsLetter(compact[0])
            && char.IsLetter(compact[1])
            && char.IsDigit(compact[2])
            && char.IsDigit(compact[3])
            && compact.All(char.IsLetterOrDigit);
    }

    private static string? FirstValue(Newtonsoft.Json.Linq.JObject? raw, params string[] keys)
    {
        if (raw is null)
            return null;

        foreach (var key in keys)
        {
            var value = raw[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Pins <see cref="BankLinkCharges.SessionApiVersion"/> on this call. <c>StripeVersion</c> is internal on
    /// Stripe.net 50; the public client would otherwise send <c>2025-12-15.clover</c>.
    /// </summary>
    internal static void PinSessionApiVersion(RequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var property = typeof(RequestOptions).GetProperty(
            "StripeVersion",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.SetMethod is null)
            throw new InvalidOperationException("Stripe.net RequestOptions.StripeVersion is not settable.");

        property.SetValue(options, BankLinkCharges.SessionApiVersion);
    }

    internal static string? ReadSessionApiVersion(RequestOptions options)
    {
        var property = typeof(RequestOptions).GetProperty(
            "StripeVersion",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.GetValue(options) as string;
    }

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

    private PaymentProcessingException ProviderFailure(StripeException ex)
    {
        var code = ex.StripeError?.Code ?? ex.HttpStatusCode.ToString();
        logger.LogWarning(
            "Stripe bank-link call failed. Code={Code} Type={Type} Param={Param} RequestId={RequestId} Detail={Detail}",
            code,
            ex.StripeError?.Type,
            ex.StripeError?.Param,
            ex.StripeResponse?.RequestId,
            Redact(ex.StripeError?.Message));
        return new PaymentProcessingException($"Stripe bank-link call failed ({code}).");
    }

    /// <summary>Stripe's message for the logs, with keys and IBANs removed. The client still gets the generic 503.</summary>
    internal static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var redacted = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\b(?:sk|rk|pk)_(?:test|live)_[A-Za-z0-9]+|\bwhsec_[A-Za-z0-9]+",
            "[secret]");
        // A space or a dash may sit between characters: Stripe prints IBANs in groups, not only as one uppercase token.
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"\b[A-Z]{2}\d{2}(?:[ \-]?[A-Z0-9]){11,30}\b",
            "[iban]");
        return redacted.Length <= 300 ? redacted : redacted[..300];
    }

    private static string Trim(string? value, int max)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "CasaZen" : value.Trim();
        return text.Length <= max ? text : text[..max];
    }
}
