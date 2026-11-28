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
    public static CheckoutOutcomeState StateOf(Booking booking, HoldExpiryCutoff cutoff)
    {
        ArgumentNullException.ThrowIfNull(booking);

        switch (booking.Status)
        {
            case BookingStatus.Confirmed:
            case BookingStatus.CheckedIn:
            case BookingStatus.CheckedOut:
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

        if (booking.Payments.Any(p => p.Status is PaymentStatus.Processing or PaymentStatus.Completed))
            return CheckoutOutcomeState.PaymentProcessing;

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

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool TokenMatches(string? storedHash, string? token)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return false;

        var actual = Encoding.ASCII.GetBytes(HashToken(token.Trim()));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public enum CheckoutOutcomeState
{
    Confirmed,

    AwaitingPayment,

    PaymentProcessing,

    PaymentFailed,

    AwaitingGuestEmail,

    AwaitingHostApproval,

    Expired,

    Declined,

    DatesUnavailable,

    Cancelled,
}
