using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-08 (A3-14): rules of the deferred payment. A PaymentIntent that needs the guest is never "Completed", the automatic
/// cancellation happens only before the arrival, and the outcome page shows a failed deferred charge as a payment to
/// complete.
/// </summary>
public class DeferredChargesTests
{
    private static readonly DateTime FailedAt = new(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("succeeded", PaymentStatus.Completed)]
    [InlineData("processing", PaymentStatus.Processing)]
    [InlineData("requires_capture", PaymentStatus.Processing)]
    [InlineData("requires_action", PaymentStatus.Failed)]
    [InlineData("requires_payment_method", PaymentStatus.Failed)]
    [InlineData("requires_confirmation", PaymentStatus.Failed)]
    [InlineData("canceled", PaymentStatus.Canceled)]
    [InlineData(null, PaymentStatus.Failed)]
    public void StatusOf_PaymentIntentStatus_IsCompletedOnlyWhenSucceeded(string? intentStatus, PaymentStatus expected)
    {
        Assert.Equal(expected, DeferredCharges.StatusOf(intentStatus));
    }

    [Fact]
    public void CreationIdempotencyKey_BookingDeadlineAndAttempt_AreAllInTheKey()
    {
        var bookingId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var deadline = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            "direct-booking-deadline:11111111222233334444555555555555:20261001:2",
            DeferredCharges.CreationIdempotencyKey(bookingId, deadline, 2));
        Assert.NotEqual(
            DeferredCharges.CreationIdempotencyKey(bookingId, deadline, 1),
            DeferredCharges.CreationIdempotencyKey(bookingId, deadline.AddDays(1), 1));
    }

    [Fact]
    public void Settings_NotConfigured_UseTheProvisionalDefaults()
    {
        var configuration = Configuration();

        Assert.Equal(DeferredCharges.ProvisionalMaxAttempts, DeferredCharges.GetMaxAttempts(configuration));
        Assert.Equal(DeferredCharges.ProvisionalCancelAfterDays, DeferredCharges.GetCancelAfterDays(configuration));
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("-2", null)]
    [InlineData("5", 5)]
    public void GetCancelAfterDays_Configured_ZeroOrLessDisablesTheCancellation(string value, int? expected)
    {
        Assert.Equal(expected, DeferredCharges.GetCancelAfterDays(Configuration((DeferredCharges.CancelAfterDaysSetting, value))));
    }

    [Fact]
    public void GetMaxAttempts_BelowOne_IsOne()
    {
        Assert.Equal(1, DeferredCharges.GetMaxAttempts(Configuration((DeferredCharges.MaxAttemptsSetting, "0"))));
    }

    [Fact]
    public void CancellationDay_BeforeTheArrival_IsFirstFailureDayPlusN()
    {
        var booking = FailedBooking(checkIn: new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 10, 4), DeferredCharges.CancellationDay(booking, 3));
        // Start of 4 October in Rome (CEST): 22:00 UTC of 3 October.
        Assert.Equal(new DateTime(2026, 10, 3, 22, 0, 0, DateTimeKind.Utc), DeferredCharges.PayByUtc(booking, 3));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public void CancellationDay_OnOrAfterTheCheckInDay_DoesNotApply(int checkInDay)
    {
        var booking = FailedBooking(checkIn: new DateTime(2026, 10, checkInDay, 0, 0, 0, DateTimeKind.Utc));

        // 1 October + 3 days is the check-in day or later: the stay may have started, the host decides.
        Assert.Null(DeferredCharges.CancellationDay(booking, 3));
        Assert.Null(DeferredCharges.PayByUtc(booking, 3));
    }

    [Fact]
    public void CancellationDay_DisabledOrNothingFailed_IsNull()
    {
        var failed = FailedBooking(checkIn: new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        var notFailed = FailedBooking(checkIn: new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        notFailed.DeferredChargeFailedAt = null;

        Assert.Null(DeferredCharges.CancellationDay(failed, null));
        Assert.Null(DeferredCharges.CancellationDay(notFailed, 3));
    }

    [Fact]
    public void StateOf_ConfirmedBookingWithFailedDeferredCharge_IsPaymentFailedUntilTheCancellation()
    {
        var booking = FailedBooking(checkIn: new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        var cutoff = CheckoutHolds.CutoffAt(FailedAt.AddHours(1), 30);

        var state = CheckoutOutcomes.StateOf(booking, cutoff);

        Assert.Equal(CheckoutOutcomeState.PaymentFailed, state);
        Assert.True(DeferredCharges.AwaitsGuestPayment(booking));
        Assert.Equal(new DateTime(2026, 10, 3, 22, 0, 0, DateTimeKind.Utc), CheckoutOutcomes.ExpiresAt(booking, state, 30, 3));
        Assert.Null(CheckoutOutcomes.ExpiresAt(booking, state, 30, null));
    }

    [Theory]
    [InlineData(PaymentStatus.Completed)]
    [InlineData(PaymentStatus.Processing)]
    public void StateOf_ConfirmedBookingWhoseDeferredChargeIsPaidOrPaying_IsConfirmed(PaymentStatus status)
    {
        var booking = FailedBooking(checkIn: new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(booking.Payments).Status = status;

        Assert.Equal(
            CheckoutOutcomeState.Confirmed,
            CheckoutOutcomes.StateOf(booking, CheckoutHolds.CutoffAt(FailedAt, 30)));
        Assert.False(DeferredCharges.AwaitsGuestPayment(booking));
    }

    [Fact]
    public void FindPayment_CollectedAndNewerRows_PrefersTheCollectedOne()
    {
        var booking = FailedBooking(checkIn: new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        var failed = Assert.Single(booking.Payments);
        failed.UpdatedAt = FailedAt.AddDays(2);
        var legacy = new Payment
        {
            BookingId = booking.Id,
            Status = PaymentStatus.Completed,
            Description = DeferredCharges.LegacyPaymentDescription,
            UpdatedAt = FailedAt,
        };
        var other = new Payment { BookingId = booking.Id, Status = PaymentStatus.Completed, Description = "Direct checkout" };
        booking.Payments.Add(legacy);
        booking.Payments.Add(other);

        Assert.Same(legacy, DeferredCharges.FindPayment(booking, booking.Payments));
    }

    private static Booking FailedBooking(DateTime checkIn)
    {
        var booking = new Booking
        {
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.OnCancellationDeadline,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            FreeRefundDeadline = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            StripeSetupIntentId = "seti_1",
            DeferredChargeFailedAt = FailedAt,
            CreatedAt = FailedAt.AddDays(-20),
        };
        booking.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            Status = PaymentStatus.Failed,
            TransactionId = "pi_1",
            StripePaymentIntentId = "pi_1",
            Description = DeferredCharges.PaymentDescription,
            UpdatedAt = FailedAt,
        });
        return booking;
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
