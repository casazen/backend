using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Stripe.FinancialConnections;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The linked-account debit uses the existing Stripe client. A Financial Connections account id is sent; an IBAN is not.
/// SEPA credit transfer is not requested. USD ACH (<c>us_bank_account</c>) is the debit Stripe can create from that id.
/// </summary>
public class StripeBankLinkGatewayTests
{
    private readonly Mock<IStripeClient> _client = new();
    private readonly List<BaseOptions> _sent = [];

    public StripeBankLinkGatewayTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
    }

    [Fact]
    public async Task CreateSessionAsync_AsksForThePaymentMethodPermission_OfTheCustomer()
    {
        _client.Setup(c => c.RequestAsync<Session>(
                HttpMethod.Post,
                "/v1/financial_connections/sessions",
                It.IsAny<BaseOptions>(),
                It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, _, _) => _sent.Add(options))
            .ReturnsAsync(new Session { Id = "fcsess_1", ClientSecret = "fcsess_secret" });

        var session = await Gateway().CreateSessionAsync("cus_landlord");

        Assert.Equal("fcsess_1", session.SessionId);
        Assert.Equal("fcsess_secret", session.ClientSecret);
        var options = Assert.IsType<SessionCreateOptions>(_sent.Single());
        Assert.Equal("customer", options.AccountHolder.Type);
        Assert.Equal("cus_landlord", options.AccountHolder.Customer);
        Assert.Equal("payment_method", Assert.Single(options.Permissions));
    }

    [Fact]
    public async Task StartDebitAsync_CreatesUsBankAccountPaymentIntent_FromTheLinkedAccountId()
    {
        PaymentMethodCreateOptions? method = null;
        PaymentIntentCreateOptions? intent = null;
        _client.Setup(c => c.RequestAsync<PaymentMethod>(
                HttpMethod.Post, "/v1/payment_methods", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, _, _) => method = (PaymentMethodCreateOptions)options)
            .ReturnsAsync(new PaymentMethod { Id = "pm_linked" });
        _client.Setup(c => c.RequestAsync<PaymentIntent>(
                HttpMethod.Post, "/v1/payment_intents", It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<HttpMethod, string, BaseOptions, RequestOptions, CancellationToken>((_, _, options, _, _) => intent = (PaymentIntentCreateOptions)options)
            .ReturnsAsync(new PaymentIntent { Id = "pi_linked", Status = "processing", ClientSecret = "pi_secret" });

        var started = await Gateway().StartDebitAsync(Command("usd", [BankLinkCharges.AchDebit]));

        Assert.Equal("pi_linked", started.PaymentIntentId);
        Assert.Equal(BankLinkCharges.AchDebit, started.PaymentMethodType);
        Assert.Equal("processing", started.Status);
        Assert.Equal("fca_linked", method!.UsBankAccount.FinancialConnectionsAccount);
        Assert.Null(method.SepaDebit);
        Assert.Equal("us_bank_account", method.Type);
        Assert.Equal(2_500, intent!.Amount);
        Assert.Equal("usd", intent.Currency);
        Assert.True(intent.Confirm);
        Assert.Equal("us_bank_account", Assert.Single(intent.PaymentMethodTypes));
        Assert.Equal("bank-link-payment", intent.Metadata["kind"]);
    }

    [Fact]
    public async Task StartDebitAsync_EurRent_DoesNotCallStripe()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Gateway().StartDebitAsync(Command("eur", [BankLinkCharges.AchDebit, "sepa_debit"])));

        Assert.Equal(BankLinkErrorCodes.DebitUnavailable, ex.Code);
        _client.Verify(
            c => c.RequestAsync<PaymentIntent>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void CanDebit_IsUsdAchOnly()
    {
        Assert.True(StripeBankLinkGateway.CanDebit([BankLinkCharges.AchDebit], "usd"));
        Assert.False(StripeBankLinkGateway.CanDebit([BankLinkCharges.AchDebit], "eur"));
        Assert.False(StripeBankLinkGateway.CanDebit(["sepa_debit"], "eur"));
        Assert.False(StripeBankLinkGateway.CanDebit(["link"], "eur"));
        Assert.False(StripeBankLinkGateway.IsAccountId("IT60X0542811101000000123456"));
        Assert.True(StripeBankLinkGateway.IsAccountId("fca_1Nlinked"));
    }

    [Fact]
    public async Task CreateSessionAsync_WithoutAKey_FailsBeforeStripe()
    {
        var gateway = new StripeBankLinkGateway(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:SecretKey"] = "sk_test_YOUR_KEY" }).Build(),
            NullLogger<StripeBankLinkGateway>.Instance);

        await Assert.ThrowsAsync<PaymentProcessingException>(() => gateway.CreateSessionAsync("cus_landlord"));
    }

    private StripeBankLinkGateway Gateway() =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:SecretKey"] = "sk_test_unit" }).Build(),
            NullLogger<StripeBankLinkGateway>.Instance,
            _client.Object);

    private static BankDebitCommand Command(string currency, IReadOnlyList<string> types) =>
        new(
            "cus_landlord",
            "fca_linked",
            types,
            2_500,
            currency,
            "bank-link-key",
            "Casa Rossi",
            "203.0.113.5",
            "CasaZen",
            new Dictionary<string, string> { ["kind"] = BankLinkCharges.Kind });
}
