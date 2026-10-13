using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-07 (A3-15): the outcome page reads the real state of the checkout, never "confirmed" before the confirmation.
/// </summary>
public class CheckoutOutcomesTests
{
    private const int TtlMinutes = 30;
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private static readonly HoldExpiryCutoff Cutoff = CheckoutHolds.CutoffAt(Now, TtlMinutes);

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.CheckedIn)]
    [InlineData(BookingStatus.CheckedOut)]
    public void StateOf_ConfirmedBooking_IsConfirmed(BookingStatus status)
    {
        var booking = PaymentHold(PaymentStatus.Completed);
        booking.Status = status;

        Assert.Equal(CheckoutOutcomeState.Confirmed, CheckoutOutcomes.StateOf(booking, Cutoff));
    }

    [Fact]
    public void StateOf_PendingHoldNotPaidYet_IsAwaitingPaymentUntilTheEndOfTheHold()
    {
        var booking = PaymentHold(PaymentStatus.Pending);

        var state = CheckoutOutcomes.StateOf(booking, Cutoff);

        Assert.Equal(CheckoutOutcomeState.AwaitingPayment, state);
        Assert.Equal(booking.CreatedAt.AddMinutes(TtlMinutes), CheckoutOutcomes.ExpiresAt(booking, state, TtlMinutes));
    }

    [Fact]
    public void StateOf_PaymentSucceededButWebhookNotConfirmedYet_IsProcessingNotConfirmed()
    {
        // The webhook confirms the booking; until then the guest must not read "Prenotazione confermata".
        Assert.Equal(
            CheckoutOutcomeState.PaymentProcessing,
            CheckoutOutcomes.StateOf(PaymentHold(PaymentStatus.Completed), Cutoff));
    }

    [Fact]
    public void StateOf_SepaDebitProcessingPastTheTtl_IsProcessingNotExpired()
    {
        var booking = PaymentHold(PaymentStatus.Processing, createdMinutesAgo: 120);

        Assert.Equal(CheckoutOutcomeState.PaymentProcessing, CheckoutOutcomes.StateOf(booking, Cutoff));
    }

    [Fact]
    public void StateOf_LastAttemptFailedWithinTheHold_IsPaymentFailed()
    {
        Assert.Equal(
            CheckoutOutcomeState.PaymentFailed,
            CheckoutOutcomes.StateOf(PaymentHold(PaymentStatus.Failed), Cutoff));
    }

    [Fact]
    public void StateOf_HoldPastTheTtlNotCancelledByTheJobYet_IsExpired()
    {
        var booking = PaymentHold(PaymentStatus.Pending, createdMinutesAgo: TtlMinutes + 1);

        var state = CheckoutOutcomes.StateOf(booking, Cutoff);

        Assert.Equal(CheckoutOutcomeState.Expired, state);
        Assert.Null(CheckoutOutcomes.ExpiresAt(booking, state, TtlMinutes));
    }

    [Theory]
    [InlineData(BookingCancellationReason.CheckoutHoldExpired, CheckoutOutcomeState.Expired)]
    [InlineData(BookingCancellationReason.OnSiteRequestExpired, CheckoutOutcomeState.Expired)]
    [InlineData(BookingCancellationReason.OnSiteEmailNotConfirmed, CheckoutOutcomeState.Expired)]
    [InlineData(BookingCancellationReason.OnSiteRequestDeclined, CheckoutOutcomeState.Declined)]
    [InlineData(BookingCancellationReason.DatesUnavailableAtPayment, CheckoutOutcomeState.DatesUnavailable)]
    [InlineData(null, CheckoutOutcomeState.Cancelled)]
    public void StateOf_CancelledBooking_TellsWhy(BookingCancellationReason? reason, CheckoutOutcomeState expected)
    {
        var booking = PaymentHold(PaymentStatus.Canceled);
        booking.Status = BookingStatus.Cancelled;
        booking.CancellationReason = reason;

        Assert.Equal(expected, CheckoutOutcomes.StateOf(booking, Cutoff));
    }

    [Fact]
    public void StateOf_PayAtThePropertyRequest_FollowsTheEmailThenTheHost()
    {
        var booking = new Booking
        {
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.OnSite,
            CreatedAt = Now.AddMinutes(-5),
            RequestExpiresAt = Now.AddMinutes(25),
        };

        Assert.Equal(CheckoutOutcomeState.AwaitingGuestEmail, CheckoutOutcomes.StateOf(booking, Cutoff));

        booking.GuestEmailVerifiedAt = Now.AddMinutes(-1);
        booking.RequestExpiresAt = Now.AddHours(24);
        var state = CheckoutOutcomes.StateOf(booking, Cutoff);
        Assert.Equal(CheckoutOutcomeState.AwaitingHostApproval, state);
        Assert.Equal(booking.RequestExpiresAt, CheckoutOutcomes.ExpiresAt(booking, state, TtlMinutes));

        booking.RequestExpiresAt = Now.AddMinutes(-1);
        Assert.Equal(CheckoutOutcomeState.Expired, CheckoutOutcomes.StateOf(booking, Cutoff));
    }

    [Fact]
    public void TokenMatches_OnlyTheTokenWhoseHashIsStored()
    {
        var token = CheckoutOutcomes.NewToken();
        var hash = CheckoutOutcomes.HashToken(token);

        Assert.Equal(64, hash.Length);
        Assert.True(CheckoutOutcomes.TokenMatches(hash, token));
        Assert.False(CheckoutOutcomes.TokenMatches(hash, CheckoutOutcomes.NewToken()));
        Assert.False(CheckoutOutcomes.TokenMatches(null, token));
        Assert.False(CheckoutOutcomes.TokenMatches(hash, null));
        Assert.False(CheckoutOutcomes.TokenMatches(hash, new string('a', 129)));
        Assert.NotEqual(token, CheckoutOutcomes.NewToken());
    }

    private static Booking PaymentHold(PaymentStatus paymentStatus, int createdMinutesAgo = 5)
    {
        var booking = new Booking
        {
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            CreatedAt = Now.AddMinutes(-createdMinutesAgo),
        };
        booking.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            Status = paymentStatus,
            StripePaymentIntentId = "pi_test_outcome",
            UpdatedAt = Now.AddMinutes(-1),
        });
        return booking;
    }
}
