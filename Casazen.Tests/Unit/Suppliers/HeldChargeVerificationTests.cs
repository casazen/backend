using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15c-a: what stands between Stripe's word and a payment held by CasaZen. A block, a charge or a transfer is accepted only if it is exactly
/// what CasaZen asked for: the platform account, the PaymentIntent it created, the price and the currency, no application fee at all; for the
/// transfer, the amount that was meant and the supplier's account, not reversed. Anything else sends the payment to review instead of
/// showing it as held or paid.
/// </summary>
public class HeldChargeVerificationTests
{
    private const string IntentId = "pi_held_1";
    private const string Supplier = "acct_supplier";

    private static readonly HeldChargeExpectation Expected = new(IntentId, 6_000, "eur");

    private static HeldChargeObservation Block(
        string intent = IntentId,
        string? account = null,
        long amount = 6_000,
        long capturable = 6_000,
        long received = 0,
        string? currency = "eur",
        long? fee = null) =>
        new(intent, account, amount, capturable, received, currency, fee);

    private static HeldChargeObservation Charge(
        string intent = IntentId,
        string? account = null,
        long amount = 6_000,
        long received = 6_000,
        string? currency = "eur",
        long? fee = null) =>
        new(intent, account, amount, 0, received, currency, fee);

    private static string[] ForBlock(HeldChargeObservation observed) =>
        HeldChargeVerification.AuthorizationMismatches(Expected, observed).ToArray();

    private static string[] ForCharge(HeldChargeObservation observed, HeldChargeExpectation? expected = null) =>
        HeldChargeVerification.CaptureMismatches(expected ?? Expected, observed).ToArray();

    private static string[] ForTransfer(long meant, string? supplier, long observed, string? destination, bool reversed = false) =>
        HeldChargeVerification.TransferMismatches(meant, supplier, observed, destination, reversed).ToArray();

    [Fact]
    public void ABlockForThePriceOnThePlatform_IsAccepted() => Assert.Empty(ForBlock(Block()));

    [Fact]
    public void AChargeForThePriceOnThePlatform_IsAccepted() => Assert.Empty(ForCharge(Charge()));

    [Theory]
    [InlineData(5_999, 5_999)]
    [InlineData(6_001, 6_001)]
    [InlineData(6_000, 5_000)] // blocked for less than the price
    [InlineData(6_000, 0)] // nothing left to charge
    public void ABlock_ForAnotherAmountThanThePrice_NeedsAReview(long amount, long capturable) =>
        Assert.Equal(["amount"], ForBlock(Block(amount: amount, capturable: capturable)));

    [Fact]
    public void ABlock_ThatWasAlreadyCharged_NeedsAReview() => Assert.Equal(["amount"], ForBlock(Block(received: 6_000)));

    [Theory]
    [InlineData(6_000, 5_999)]
    [InlineData(6_000, 0)]
    [InlineData(6_000, 6_001)]
    [InlineData(5_000, 5_000)]
    public void AChargeOfAnotherAmountThanThePrice_NeedsAReview(long amount, long received) =>
        Assert.Equal(["amount"], ForCharge(Charge(amount: amount, received: received)));

    [Fact]
    public void AnEventOfAConnectedAccount_IsNotAboutAHeldPayment() =>
        Assert.Equal(["account"], ForCharge(Charge(account: "acct_other")));

    [Fact]
    public void AnotherCurrency_NeedsAReview() => Assert.Equal(["currency"], ForCharge(Charge(currency: "usd")));

    [Fact]
    public void TheCurrencyIsComparedWithoutCase() => Assert.Empty(ForCharge(Charge(currency: "EUR")));

    [Theory]
    [InlineData(1L)]
    [InlineData(600L)]
    [InlineData(5_999L)]
    public void AnApplicationFee_IsNeverExpected_BecauseTheCommissionIsWhatIsNotTransferred(long fee)
    {
        Assert.Equal(["fee"], ForCharge(Charge(fee: fee)));
        Assert.Equal(["fee"], ForBlock(Block(fee: fee)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void NoFeeAndAFeeOfZero_AreTheSame(long? fee) => Assert.Empty(ForCharge(Charge(fee: fee)));

    [Fact]
    public void AnotherPaymentIntent_ThanTheOneRecorded_NeedsAReview()
    {
        Assert.Equal(["intent"], ForCharge(Charge(intent: "pi_other")));
        Assert.Equal(["intent"], ForCharge(Charge(), new HeldChargeExpectation(null, 6_000, "eur")));
        Assert.Equal(["intent"], ForCharge(Charge(), new HeldChargeExpectation("  ", 6_000, "eur")));
    }

    [Fact]
    public void SeveralMismatches_AreReportedTogether_InAStableOrder()
    {
        var mismatches = ForCharge(Charge(intent: "pi_other", account: "acct_x", amount: 1, received: 1, currency: "usd", fee: 5));

        Assert.Equal(["account", "amount", "currency", "fee", "intent"], mismatches);
        Assert.Equal("review:account+amount+currency+fee+intent", ServiceChargeVerification.ReviewCode(mismatches));
    }

    // ─── The transfer to the supplier ───

    [Fact]
    public void ATransferOfTheMeantAmountToTheSupplier_IsAccepted() =>
        Assert.Empty(ForTransfer(5_400, Supplier, 5_400, Supplier));

    [Theory]
    [InlineData(5_399L)]
    [InlineData(5_401L)]
    [InlineData(6_000L)] // the whole price: the commission would go to the supplier
    public void ATransferOfAnotherAmount_NeedsAReview(long observed) =>
        Assert.Equal(["amount"], ForTransfer(5_400, Supplier, observed, Supplier));

    [Fact]
    public void ATransferToAnotherAccount_NeedsAReview()
    {
        Assert.Equal(["destination"], ForTransfer(5_400, Supplier, 5_400, "acct_other"));
        Assert.Equal(["destination"], ForTransfer(5_400, Supplier, 5_400, null));
        Assert.Equal(["destination"], ForTransfer(5_400, null, 5_400, Supplier));
    }

    [Fact]
    public void AReversedTransfer_IsNotAPaymentToTheSupplier() =>
        Assert.Equal(["reversed"], ForTransfer(5_400, Supplier, 5_400, Supplier, reversed: true));
}
