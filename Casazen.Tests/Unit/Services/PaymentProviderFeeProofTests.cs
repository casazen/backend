using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F04/US01/T02 (FIS-05, NF-85): the commission and the net Stripe reports for a settled card payment are stored to the cent, read on the right
/// account, once, within five attempts, and the capture never touches the amount, the refunded amount or the status of the payment (money rules).
/// </summary>
public class PaymentProviderFeeProofTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(12345L, 360L, 11985L, "eur", "3.60", "119.85")]
    [InlineData(99L, 32L, 67L, "chf", "0.32", "0.67")]
    [InlineData(1000001L, 14251L, 985750L, "EUR", "142.51", "9857.50")]
    public async Task PaymentProviderFee_AmountsFromBalanceTransaction_CentAccurate_Test(
        long grossMinor, long feeMinor, long netMinor, string currency, string feeText, string netText)
    {
        var fee = decimal.Parse(feeText, System.Globalization.CultureInfo.InvariantCulture);
        var net = decimal.Parse(netText, System.Globalization.CultureInfo.InvariantCulture);
        await using var db = NewDb();
        var payment = await SeedPaymentAsync(db, grossMinor / 100m);
        var stripe = StripeAnswering(new StripeBalanceTransactionInfo("txn_TEST_1", feeMinor, netMinor, currency, new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc)));

        var outcome = await Service(db, stripe.Object).CaptureAsync(payment.Id);

        Assert.Equal(PaymentProviderFeeOutcome.Captured, outcome);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(fee, stored.ProviderFeeAmount);
        Assert.Equal(net, stored.ProviderNetAmount);
        Assert.Equal(currency.ToUpperInvariant(), stored.ProviderBalanceCurrency);
        Assert.Equal("txn_TEST_1", stored.ProviderBalanceTransactionId);
        Assert.Equal(new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), stored.ProviderAvailableOn);
        Assert.Equal(Now.UtcDateTime, stored.ProviderFeeFetchedAt);
        Assert.Equal(stored.ProviderFeeAmount + stored.ProviderNetAmount, grossMinor / 100m);
    }

    [Fact]
    public async Task PaymentProviderFee_NeverChangesPaymentAmounts_Test()
    {
        await using var db = NewDb();
        var payment = await SeedPaymentAsync(db, 250m, refunded: 50m, status: PaymentStatus.PartiallyRefunded);
        var stripe = StripeAnswering(new StripeBalanceTransactionInfo("txn_TEST_2", 900L, 24100L, "eur", Now.UtcDateTime));

        await Service(db, stripe.Object).CaptureAsync(payment.Id);

        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(250m, stored.Amount);
        Assert.Equal(50m, stored.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, stored.Status);
        Assert.Equal(9.00m, stored.ProviderFeeAmount);
    }

    [Fact]
    public async Task Capture_ReadsADirectChargeOnItsConnectedAccount_AndAPlatformIntentWithoutTheHeader()
    {
        await using var db = NewDb();
        var direct = await SeedPaymentAsync(db, 100m, account: "acct_TEST_direct");
        var platform = await SeedPaymentAsync(db, 100m, onPlatform: true);
        var stripe = StripeAnswering(new StripeBalanceTransactionInfo("txn_TEST_3", 100L, 9900L, "eur", Now.UtcDateTime));
        var service = Service(db, stripe.Object);

        await service.CaptureAsync(direct.Id);
        await service.CaptureAsync(platform.Id);

        stripe.Verify(s => s.GetChargeBalanceTransactionAsync(direct.StripePaymentIntentId!, "acct_TEST_direct", It.IsAny<CancellationToken>()), Times.Once);
        stripe.Verify(s => s.GetChargeBalanceTransactionAsync(platform.StripePaymentIntentId!, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Capture_IsIdempotent_AndStopsAfterFiveAttempts()
    {
        await using var db = NewDb();
        var captured = await SeedPaymentAsync(db, 100m);
        var pending = await SeedPaymentAsync(db, 100m);
        var stripe = new Mock<IStripeService>();
        stripe.Setup(s => s.GetChargeBalanceTransactionAsync(captured.StripePaymentIntentId!, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeBalanceTransactionInfo("txn_TEST_4", 100L, 9900L, "eur", Now.UtcDateTime));
        stripe.Setup(s => s.GetChargeBalanceTransactionAsync(pending.StripePaymentIntentId!, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Stripe.StripeException { StripeError = new Stripe.StripeError { Code = "resource_missing" } });
        var service = Service(db, stripe.Object);

        Assert.Equal(PaymentProviderFeeOutcome.Captured, await service.CaptureAsync(captured.Id));
        Assert.Equal(PaymentProviderFeeOutcome.AlreadyCaptured, await service.CaptureAsync(captured.Id));
        for (var attempt = 0; attempt < PaymentProviderFees.MaxAttempts; attempt++)
            Assert.Equal(PaymentProviderFeeOutcome.Failed, await service.CaptureAsync(pending.Id));
        Assert.Equal(PaymentProviderFeeOutcome.AttemptsExhausted, await service.CaptureAsync(pending.Id));

        stripe.Verify(s => s.GetChargeBalanceTransactionAsync(captured.StripePaymentIntentId!, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        stripe.Verify(s => s.GetChargeBalanceTransactionAsync(pending.StripePaymentIntentId!, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        Assert.Equal(5, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == pending.Id)).ProviderFeeFetchAttempts);
        Assert.DoesNotContain(pending.Id, await service.DuePaymentIdsAsync(PaymentProviderFees.BatchSize));
    }

    [Fact]
    public async Task DuePayments_AreSettledCardPaymentsOlderThanTenMinutesWithoutFigures()
    {
        await using var db = NewDb();
        var due = await SeedPaymentAsync(db, 100m);
        var recent = await SeedPaymentAsync(db, 100m, processedAt: Now.UtcDateTime.AddMinutes(-5));
        var pendingStatus = await SeedPaymentAsync(db, 100m, status: PaymentStatus.Pending);
        var cash = await SeedPaymentAsync(db, 100m, paymentIntent: null);

        var ids = await Service(db, new Mock<IStripeService>().Object).DuePaymentIdsAsync(PaymentProviderFees.BatchSize);

        Assert.Equal(new[] { due.Id }, ids);
        Assert.DoesNotContain(recent.Id, ids);
        Assert.DoesNotContain(pendingStatus.Id, ids);
        Assert.DoesNotContain(cash.Id, ids);
    }

    [Fact]
    public async Task Job_WithoutAStripeKey_ReadsNothingAndCountsNoAttempt()
    {
        var fees = new Mock<IPaymentProviderFeeService>(MockBehavior.Strict);
        var job = new PaymentProviderFeeJob(fees.Object, new ConfigurationBuilder().Build(), NullLogger<PaymentProviderFeeJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        fees.VerifyNoOtherCalls();
        var attribute = typeof(PaymentProviderFeeJob).GetMethod(nameof(PaymentProviderFeeJob.ExecuteAsync))!
            .GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), false);
        Assert.Single(attribute);
        Assert.Equal("payment-provider-fees", PaymentProviderFeeJob.RecurringJobId);
        Assert.Equal("*/15 * * * *", PaymentProviderFeeJob.Cron);
    }

    private static Mock<IStripeService> StripeAnswering(StripeBalanceTransactionInfo info)
    {
        var stripe = new Mock<IStripeService>();
        stripe.Setup(s => s.GetChargeBalanceTransactionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        return stripe;
    }

    private static PaymentProviderFeeService Service(AppDbContext db, IStripeService stripe) =>
        new(db, stripe, new FakeTimeProvider(Now), NullLogger<PaymentProviderFeeService>.Instance);

    private static AppDbContext NewDb() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"ProviderFees_{Guid.NewGuid()}").Options,
            new FeeTenant(),
            new EphemeralDataProtectionProvider());

    private static async Task<Payment> SeedPaymentAsync(
        AppDbContext db,
        decimal amount,
        decimal refunded = 0m,
        PaymentStatus status = PaymentStatus.Completed,
        string? account = null,
        bool onPlatform = false,
        DateTime? processedAt = null,
        string? paymentIntent = "")
    {
        var payment = new Payment
        {
            OrgId = OrgId,
            BookingId = Guid.NewGuid(),
            Amount = amount,
            RefundedAmount = refunded,
            Status = status,
            Method = PaymentMethod.CreditCard,
            StripePaymentIntentId = paymentIntent is null ? null : $"pi_TEST_{Guid.NewGuid():N}",
            StripeAccountId = account,
            StripeIntentOnPlatform = onPlatform,
            ProcessedAt = processedAt ?? Now.UtcDateTime.AddHours(-1),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }
}

file sealed class FeeTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
