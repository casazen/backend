using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15b, decision D2: a succeeded PaymentIntent makes a service payment "Pagato" only if the account, the amount received, the
/// currency, the commission and the PaymentIntent are the ones the payment snapshotted. Every difference is named, so the admin
/// who reviews it knows what to look at; nothing else is ever "paid".
/// </summary>
public class ServiceChargeVerificationTests
{
    private static readonly ServiceChargeExpectation Expected = new("acct_supplier", "pi_1", 6_000, "eur", 600);

    private static ServiceChargeObservation Matching() => new("pi_1", "acct_supplier", 6_000, 6_000, "eur", 600);

    [Fact]
    public void Mismatches_EverythingAsAskedFor_IsEmpty()
    {
        Assert.Empty(ServiceChargeVerification.Mismatches(Expected, Matching()));
    }

    [Fact]
    public void Mismatches_TheCurrencyIsComparedWithoutCase()
    {
        Assert.Empty(ServiceChargeVerification.Mismatches(Expected, Matching() with { Currency = "EUR" }));
    }

    [Theory]
    [InlineData("acct_other")]
    [InlineData("")]
    [InlineData(null)]
    public void Mismatches_AnotherAccountOrNone_IsAnAccountMismatch(string? account)
    {
        var mismatches = ServiceChargeVerification.Mismatches(Expected, Matching() with { AccountId = account });

        Assert.Equal([ServiceChargeVerification.AccountMismatch], mismatches);
    }

    [Fact]
    public void Mismatches_APaymentWithNoRecordedAccount_CanNeverBeConfirmedByAnEvent()
    {
        var expected = Expected with { ConnectedAccountId = null };

        Assert.Contains(ServiceChargeVerification.AccountMismatch, ServiceChargeVerification.Mismatches(expected, Matching()));
        Assert.Contains(ServiceChargeVerification.AccountMismatch, ServiceChargeVerification.Mismatches(expected, Matching() with { AccountId = null }));
    }

    [Theory]
    [InlineData(5_999, 6_000)]
    [InlineData(6_001, 6_000)]
    [InlineData(6_000, 5_999)]
    [InlineData(6_000, 0)]
    [InlineData(6_000, 6_001)]
    public void Mismatches_TheAmountOrTheAmountReceivedThatIsNotThePrice_IsAnAmountMismatch(long amount, long received)
    {
        var mismatches = ServiceChargeVerification.Mismatches(Expected, Matching() with { AmountCents = amount, AmountReceivedCents = received });

        Assert.Equal([ServiceChargeVerification.AmountMismatch], mismatches);
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("")]
    [InlineData(null)]
    public void Mismatches_AnotherCurrency_IsACurrencyMismatch(string? currency)
    {
        var mismatches = ServiceChargeVerification.Mismatches(Expected, Matching() with { Currency = currency });

        Assert.Equal([ServiceChargeVerification.CurrencyMismatch], mismatches);
    }

    [Theory]
    [InlineData(599L)]
    [InlineData(601L)]
    [InlineData(0L)]
    [InlineData(null)]
    public void Mismatches_ACommissionThatIsNotTheSnapshot_IsAFeeMismatch(long? fee)
    {
        var mismatches = ServiceChargeVerification.Mismatches(Expected, Matching() with { ApplicationFeeCents = fee });

        Assert.Equal([ServiceChargeVerification.FeeMismatch], mismatches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void Mismatches_APaymentWithoutCommission_AcceptsNoFeeAndAFeeOfZero(long? fee)
    {
        // The gateway never sends a zero, so Stripe reports none; a zero is the same thing.
        var expected = Expected with { ApplicationFeeCents = 0 };

        Assert.Empty(ServiceChargeVerification.Mismatches(expected, Matching() with { ApplicationFeeCents = fee }));
    }

    [Fact]
    public void Mismatches_AFeeOnAPaymentThatWasAskedForNone_IsAFeeMismatch()
    {
        var mismatches = ServiceChargeVerification.Mismatches(Expected with { ApplicationFeeCents = 0 }, Matching());

        Assert.Equal([ServiceChargeVerification.FeeMismatch], mismatches);
    }

    [Theory]
    [InlineData("pi_2")]
    [InlineData("")]
    public void Mismatches_AnotherPaymentIntentThanTheOneRecorded_IsAnIntentMismatch(string intent)
    {
        var mismatches = ServiceChargeVerification.Mismatches(Expected, Matching() with { PaymentIntentId = intent });

        Assert.Equal([ServiceChargeVerification.IntentMismatch], mismatches);
    }

    [Fact]
    public void Mismatches_NoPaymentIntentRecorded_IsAnIntentMismatch()
    {
        var mismatches = ServiceChargeVerification.Mismatches(Expected with { PaymentIntentId = null }, Matching());

        Assert.Equal([ServiceChargeVerification.IntentMismatch], mismatches);
    }

    [Fact]
    public void Mismatches_EveryDifferenceIsNamed_InAStableOrder()
    {
        var mismatches = ServiceChargeVerification.Mismatches(
            Expected,
            new ServiceChargeObservation("pi_9", "acct_other", 1, 1, "usd", null));

        Assert.Equal(
            [
                ServiceChargeVerification.AccountMismatch,
                ServiceChargeVerification.AmountMismatch,
                ServiceChargeVerification.CurrencyMismatch,
                ServiceChargeVerification.FeeMismatch,
                ServiceChargeVerification.IntentMismatch,
            ],
            mismatches);
    }

    [Fact]
    public void ReviewCode_ListsTheMismatches_AndFitsTheColumn()
    {
        var all = new[]
        {
            ServiceChargeVerification.AccountMismatch,
            ServiceChargeVerification.AmountMismatch,
            ServiceChargeVerification.CurrencyMismatch,
            ServiceChargeVerification.FeeMismatch,
            ServiceChargeVerification.IntentMismatch,
        };

        Assert.Equal("review:account+fee", ServiceChargeVerification.ReviewCode([ServiceChargeVerification.AccountMismatch, ServiceChargeVerification.FeeMismatch]));
        Assert.StartsWith(ServiceChargeVerification.ReviewPrefix, ServiceChargeVerification.ReviewCode(all), StringComparison.Ordinal);
        Assert.InRange(ServiceChargeVerification.ReviewCode(all).Length, 1, 100);
    }
}
