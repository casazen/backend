using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Stripe;
using Stripe.FinancialConnections;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The linked-account debit is SEPA in EUR. The session asks for Italian banks. A full IBAN is not kept on the account
/// CasaZen stores; a token is sent to Stripe only to create the PaymentMethod.
/// </summary>
public class StripeBankLinkGatewayTests
{
    private const string Iban = "IT60X0542811101000000123456";
    private readonly Mock<IStripeClient> _client = new();

    public StripeBankLinkGatewayTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
    }

    [Fact]
    public async Task CreateSessionAsync_AsksForItalianBanks_NotUs()
    {
        SessionCreateOptions? sent = null;
        _client.Setup(c => c.RequestAsync<Session>(
                HttpMethod.Post,
                "/v1/financial_connections/sessions",
                It.IsAny<BaseOptions>(),
                It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, _, _) => sent = (SessionCreateOptions)options)
            .ReturnsAsync(new Session { Id = "fcsess_1", ClientSecret = "fcsess_secret" });

        var session = await Gateway().CreateSessionAsync("cus_landlord");

        Assert.Equal("fcsess_1", session.SessionId);
        Assert.Equal("customer", sent!.AccountHolder.Type);
        Assert.Equal("cus_landlord", sent.AccountHolder.Customer);
        Assert.Equal("payment_method", Assert.Single(sent.Permissions));
        Assert.True(sent.Filters.Countries is null || sent.Filters.Countries.Count == 0);
        Assert.Equal(BankLinkCharges.InstitutionCountry, sent.ExtraParams["filters[country]"]);
        Assert.DoesNotContain("US", sent.ExtraParams.Values.Select(value => value?.ToString()));
        Assert.Contains("checking", sent.Filters.AccountSubcategories);
    }

    [Fact]
    public void Redact_DropsSecretsAndIbans()
    {
        var text = StripeBankLinkGateway.Redact(
            "No such country IT60X0542811101000000123456 for key sk_test_51AbCdEf and whsec_abc123");

        Assert.DoesNotContain("sk_test_", text);
        Assert.DoesNotContain("whsec_", text);
        Assert.DoesNotContain("IT60X0542811101000000123456", text);
        Assert.Contains("[secret]", text);
        Assert.Contains("[iban]", text);

        var grouped = StripeBankLinkGateway.Redact(
            "Debit failed for IT60 X054 2811 1010 0000 0123 456 (also IT60-X054-2811-1010-0000-0123-456).");
        Assert.Equal("Debit failed for [iban] (also [iban]).", grouped);
    }

    [Fact]
    public async Task StartDebitAsync_UsesTheIbanToken_ForASepaPaymentIntent()
    {
        PaymentMethodCreateOptions? method = null;
        PaymentIntentCreateOptions? intent = null;
        SetupAccount(TokenNumber("iban_tok_test"));
        SetupPayment(methodOptions => method = methodOptions, intentOptions => intent = intentOptions);

        var started = await Gateway().CreateDebitAsync(Command());

        Assert.Equal("pi_linked", started.PaymentIntentId);
        Assert.Equal(BankLinkCharges.SepaDebit, started.PaymentMethodType);
        Assert.Equal("sepa_debit", method!.Type);
        Assert.Equal("iban_tok_test", method.SepaDebit.Iban);
        Assert.Null(method.UsBankAccount);
        Assert.Equal(90_000, intent!.Amount);
        Assert.Equal("eur", intent.Currency);
        Assert.Equal("sepa_debit", Assert.Single(intent.PaymentMethodTypes));
        Assert.False(intent.Confirm);
        Assert.DoesNotContain(Iban, started.PaymentIntentId);
    }

    [Fact]
    public async Task StartDebitAsync_WithoutAToken_PassesTheLinkedAccount_NotAnIban()
    {
        PaymentMethodCreateOptions? method = null;
        SetupAccount(new Stripe.FinancialConnections.Account { Id = "fca_linked" });
        SetupPayment(methodOptions => method = methodOptions, _ => { });

        await Gateway().CreateDebitAsync(Command());

        Assert.Equal("sepa_debit", method!.Type);
        Assert.Null(method.SepaDebit.Iban);
        Assert.Null(method.UsBankAccount);
        Assert.Equal("fca_linked", method.ExtraParams["sepa_debit[financial_connections_account]"]);
    }

    [Fact]
    public async Task GetAccountAsync_DoesNotReturnAFullIban()
    {
        SetupAccount(TokenNumber(Iban));

        var account = await Gateway().GetAccountAsync("fca_linked");

        Assert.Equal("fca_linked", account.Id);
        Assert.Null(account.IbanToken);
        Assert.Null(account.SepaPaymentMethodId);
        Assert.DoesNotContain(Iban, $"{account.Id}{account.DisplayName}{account.Last4}{account.IbanToken}");
    }

    [Fact]
    public void ReadInstrument_KeepsAToken_AndRecognisesAFullIban()
    {
        var token = StripeBankLinkGateway.ReadInstrument(TokenNumber("iban_tok_test"));
        var iban = StripeBankLinkGateway.ReadInstrument(TokenNumber(Iban));

        Assert.Equal("iban_tok_test", token.IbanOrToken);
        Assert.False(StripeBankLinkGateway.IsFullIban(token.IbanOrToken));
        Assert.Equal(Iban, iban.IbanOrToken);
        Assert.True(StripeBankLinkGateway.IsFullIban(iban.IbanOrToken));
        var mapped = StripeBankLinkGateway.MapAccount(TokenNumber(Iban));
        Assert.Null(mapped.IbanToken);
        Assert.False(StripeBankLinkGateway.IsAccountId(Iban));
    }

    [Fact]
    public async Task StartDebitAsync_Usd_IsRefusedBeforeStripe()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Gateway().CreateDebitAsync(Command() with { Currency = "usd" }));

        Assert.Equal(BankLinkErrorCodes.CurrencyInvalid, ex.Code);
        _client.Verify(
            c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupAccount(Stripe.FinancialConnections.Account account) =>
        _client.Setup(c => c.RequestAsync<Stripe.FinancialConnections.Account>(
                HttpMethod.Get,
                "/v1/financial_connections/accounts/fca_linked",
                It.IsAny<BaseOptions>(),
                It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

    private void SetupPayment(Action<PaymentMethodCreateOptions> method, Action<PaymentIntentCreateOptions> intent)
    {
        _client.Setup(c => c.RequestAsync<PaymentMethod>(
                HttpMethod.Post, "/v1/payment_methods", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, _, _) => method((PaymentMethodCreateOptions)options))
            .ReturnsAsync(new PaymentMethod { Id = "pm_sepa" });
        _client.Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, "/v1/payment_intents", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, _, _) => intent((PaymentIntentCreateOptions)options))
            .ReturnsAsync(new PaymentIntent { Id = "pi_linked", Status = "processing", ClientSecret = "pi_secret" });
    }

    private static Stripe.FinancialConnections.Account TokenNumber(string value)
    {
        var number = new AccountAccountNumber
        {
            IdentifierType = "tokenized_account_number",
            Status = "transactable",
        };
        typeof(StripeEntity).GetProperty(nameof(StripeEntity.RawJObject))!
            .SetValue(number, new JObject { ["token"] = value });
        return new Stripe.FinancialConnections.Account
        {
            Id = "fca_linked",
            InstitutionName = "Intesa Sanpaolo",
            Last4 = "3456",
            AccountNumbers = [number],
        };
    }

    private StripeBankLinkGateway Gateway() =>
        new(new ConfigurationBuilder().Build(), NullLogger<StripeBankLinkGateway>.Instance, _client.Object);

    private static BankDebitCommand Command() =>
        new(
            "cus_landlord",
            "fca_linked",
            [BankLinkCharges.SepaDebit],
            90_000,
            "eur",
            "bank-link-key",
            "Casa Rossi",
            "203.0.113.5",
            "CasaZen",
            new Dictionary<string, string> { ["kind"] = BankLinkCharges.Kind });
}
