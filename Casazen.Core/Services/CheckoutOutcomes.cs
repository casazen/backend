using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Outcome of a public checkout as the guest sees it on the outcome page (BK-07, A3-15). Read from the booking and its
/// payments, never from what the browser was told: "Prenotazione confermata" only once the booking is really confirmed
/// (payment webhook, card saved, or the host's acceptance of a "pay at the property" request).
/// </summary>
/// <remarks>
/// Access needs the checkout token: 256 random bits returned once by <c>POST /api/public/bookings</c>, only its SHA-256 is
/// stored (<see cref="Booking.CheckoutTokenHash"/>). A booking id alone (visible to the host, in emails, in links) never
/// reveals the state of a booking, and a wrong id or token gets the same answer.
/// </remarks>
public static class CheckoutOutcomes
{
    /// <summary>Where the checkout of <paramref name="booking"/> stands at <paramref name="cutoff"/>. Needs its payments.</summary>
    public static CheckoutOutcomeState StateOf(Booking booking, HoldExpiryCutoff cutoff)
    {
        ArgumentNullException.ThrowIfNull(booking);

        switch (booking.Status)
        {
            case BookingStatus.Confirmed:
            case BookingStatus.CheckedIn:
            case BookingStatus.CheckedOut:
                // "Paga alla scadenza" whose deferred charge failed (BK-08): the guest must complete the payment.
                return DeferredCharges.AwaitsGuestPayment(booking)
                    ? CheckoutOutcomeState.PaymentFailed
                    : CheckoutOutcomeState.Confirmed;

            case BookingStatus.Cancelled:
                return booking.CancellationReason switch
                {
                    BookingCancellationReason.CheckoutHoldExpired or
                    BookingCancellationReason.OnSiteRequestExpired or
                    BookingCancellationReason.OnSiteEmailNotConfirmed => CheckoutOutcomeState.Expired,
                    BookingCancellationReason.OnSiteRequestDeclined => CheckoutOutcomeState.Declined,
                    BookingCancellationReason.DatesUnavailableAtPayment => CheckoutOutcomeState.DatesUnavailable,
                    _ => CheckoutOutcomeState.Cancelled,
                };
        }

        if (booking.PaymentOption == PaymentOption.OnSite)
        {
            return OnSiteRequests.StateOf(booking, cutoff.NowUtc) switch
            {
                OnSiteRequestState.AwaitingGuestEmail => CheckoutOutcomeState.AwaitingGuestEmail,
                OnSiteRequestState.AwaitingHostApproval => CheckoutOutcomeState.AwaitingHostApproval,
                _ => CheckoutOutcomeState.Expired,
            };
        }

        // Stripe said the guest has paid or is paying (webhook, or the expiry job asked Stripe): the webhook confirms the
        // booking, the dates stay taken. SEPA debits stay here for a few days.
        if (booking.Payments.Any(p => p.Status is PaymentStatus.Processing or PaymentStatus.Completed))
            return CheckoutOutcomeState.PaymentProcessing;

        // The single definition of an expired hold (BK-21): the same one that released the dates to other guests. A payment
        // that still arrives later is confirmed again or refunded by the webhook (BK-04).
        if (CheckoutHolds.IsExpired(cutoff).Compile()(booking))
            return CheckoutOutcomeState.Expired;

        var lastAttempt = booking.Payments
            .Where(p => p.StripePaymentIntentId != null)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefault();
        return lastAttempt?.Status == PaymentStatus.Failed
            ? CheckoutOutcomeState.PaymentFailed
            : CheckoutOutcomeState.AwaitingPayment;
    }

    /// <summary>
    /// Until when the guest can still pay (payment hold: creation + checkout TTL) or the request waits (pay at the property:
    /// <see cref="Booking.RequestExpiresAt"/>); <c>null</c> once the outcome no longer waits for anyone.
    /// </summary>
    /// <remarks>
    /// A failed deferred charge of a confirmed booking (BK-08) waits until its automatic cancellation
    /// (<see cref="DeferredCharges.PayByUtc"/> with <paramref name="deferredChargeCancelAfterDays"/>); <c>null</c> when none
    /// applies.
    /// </remarks>
    public static DateTime? ExpiresAt(
        Booking booking,
        CheckoutOutcomeState state,
        int ttlMinutes,
        int? deferredChargeCancelAfterDays = null) => state switch
        {
            CheckoutOutcomeState.PaymentFailed when booking.Status != BookingStatus.Pending =>
                DeferredCharges.PayByUtc(booking, deferredChargeCancelAfterDays),
            CheckoutOutcomeState.AwaitingPayment or CheckoutOutcomeState.PaymentFailed =>
                DateTime.SpecifyKind(booking.CreatedAt, DateTimeKind.Utc).AddMinutes(ttlMinutes),
            CheckoutOutcomeState.AwaitingGuestEmail or CheckoutOutcomeState.AwaitingHostApproval => booking.RequestExpiresAt,
            _ => null,
        };

    /// <summary>New random checkout token (URL-safe, 256 bits).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>What is stored for a token (<see cref="Booking.CheckoutTokenHash"/>): SHA-256, lowercase hex.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Constant-time comparison of a token with the stored hash; false when either is missing.</summary>
    public static bool TokenMatches(string? storedHash, string? token)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return false;

        var actual = Encoding.ASCII.GetBytes(HashToken(token.Trim()));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Where a checkout stands for the guest (<see cref="CheckoutOutcomes.StateOf"/>). Serialized by name.</summary>
public enum CheckoutOutcomeState
{
    /// <summary>The booking is confirmed (paid, card saved, or request accepted by the host).</summary>
    Confirmed,

    /// <summary>The hold is valid and nothing was paid yet: the guest can still pay.</summary>
    AwaitingPayment,

    /// <summary>Stripe reported the payment as succeeded or in progress (e.g. SEPA): waiting for the confirmation.</summary>
    PaymentProcessing,

    /// <summary>
    /// The last payment attempt failed; the hold is still valid, the guest can try again. Also a confirmed "Paga alla
    /// scadenza" booking whose deferred charge failed (BK-08): the guest completes the payment before the cancellation.
    /// </summary>
    PaymentFailed,

    /// <summary>"Pay at the property": waiting for the guest to confirm the email (BK-06).</summary>
    AwaitingGuestEmail,

    /// <summary>"Pay at the property": waiting for the host to accept or decline (BK-06, D5).</summary>
    AwaitingHostApproval,

    /// <summary>Not paid (or not confirmed / answered) in time: the dates were released.</summary>
    Expired,

    /// <summary>"Pay at the property" request declined by the host.</summary>
    Declined,

    /// <summary>The payment arrived after the dates were taken: the booking is cancelled and refunded in full (BK-04).</summary>
    DatesUnavailable,

    /// <summary>Cancelled for another reason (by the host).</summary>
    Cancelled,
}
