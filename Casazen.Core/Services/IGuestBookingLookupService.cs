using System.Security.Cryptography;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// "Le mie prenotazioni" of a booking site (BK-11, A3-10, R-06): the guest finds one booking of that site with its booking
/// code and the email the booking was made with. A code that does not exist, a code of another site and an email that
/// does not match get the same <see cref="Exceptions.NotFoundException"/> (<see cref="GuestBookingLookupErrorCodes.NotFound"/>),
/// so the answer never tells which bookings exist. The endpoints are rate limited per client IP and per email. The last four digits of the
/// phone (<see cref="GuestBookingPhoneCredentials"/>, NF-143) prove a booking only for the guest session (<see cref="IGuestSessionService"/>),
/// never for this lookup.
/// </summary>
public interface IGuestBookingLookupService
{
    Task<GuestBookingView> FindAsync(GuestBookingCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emails a new link of the online check-in (CO-02) to the address of the booking, when the check-in is open
    /// (<see cref="GuestCheckInAccessStatus.Open"/>): the link itself is never shown on the page, it only reaches the
    /// guest's mailbox. The previous links of the booking stop working, as with the host's "resend link".
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="GuestBookingLookupErrorCodes.CheckInLinkUnavailable"/>.</exception>
    Task SendCheckInLinkAsync(GuestBookingCredentials credentials, CancellationToken cancellationToken = default);
}

public sealed record GuestBookingCredentials(string OrgSlug, string BookingCode, string Email);

public sealed record GuestBookingPhoneCredentials(string OrgSlug, string BookingCode, string PhoneLast4)
{
    public override string ToString() => nameof(GuestBookingPhoneCredentials);
}

public static class GuestPhoneDigits
{
    public const int Count = 4;

    private const string Placeholder = "0000";

    public static bool IsValidTyped(string? typed) => typed is { Length: Count } && typed.All(char.IsAsciiDigit);

    public static string? LastFourOf(string? phone)
    {
        if (string.IsNullOrEmpty(phone))
            return null;

        Span<char> digits = stackalloc char[Count];
        var found = 0;
        for (var i = phone.Length - 1; i >= 0 && found < Count; i--)
        {
            if (char.IsAsciiDigit(phone[i]))
                digits[Count - 1 - found++] = phone[i];
        }

        return found == Count ? new string(digits) : null;
    }

    /// <summary>
    /// True when <paramref name="typed"/> are the last four digits of <paramref name="storedPhone"/>, compared in constant time
    /// (<see cref="CryptographicOperations.FixedTimeEquals"/> on equal-length bytes). The work is the same whether the booking has a phone or not and
    /// whether the typed value is valid or not (a placeholder takes the place of what is missing, and never matches), so the time tells nothing.
    /// </summary>
    public static bool Matches(string? storedPhone, string? typed)
    {
        var stored = LastFourOf(storedPhone);
        var typedIsValid = IsValidTyped(typed);
        Span<byte> expected = stackalloc byte[Count];
        Span<byte> actual = stackalloc byte[Count];
        CopyAscii(stored ?? Placeholder, expected);
        CopyAscii(typedIsValid ? typed! : Placeholder, actual);

        return CryptographicOperations.FixedTimeEquals(expected, actual) & stored is not null & typedIsValid;
    }

    private static void CopyAscii(string fourDigits, Span<byte> target)
    {
        for (var i = 0; i < Count; i++)
            target[i] = (byte)fourDigits[i];
    }
}

/// <summary>
/// One booking as its guest sees it in "Le mie prenotazioni": state, stay, amounts as recorded on the booking, in the currency of its
/// property (<paramref name="Currency"/>), the host's public contact and the online check-in. No personal data of anyone (the guest typed
/// their own email).
/// </summary>
/// <param name="Currency">ISO 4217 code of the property (<see cref="StayCurrency"/>, EUR or CHF).</param>
/// <param name="BookingCode">Formatted, <c>XXXXX-XXXXX</c>.</param>
/// <param name="Lodging">Nightly part: <c>BasePrice - CleaningFee</c>.</param>
/// <param name="TotalPrice">Lodging, cleaning and tourist tax.</param>
/// <param name="PaidAmount">Collected by Stripe (refunds included).</param>
/// <param name="RefundedAmount">Refunded so far.</param>
/// <param name="ExpiresAt">Until when the guest can pay, or the request waits; null when nothing is pending.</param>
/// <param name="DeferredChargeDate">"Paga alla scadenza": the day the saved card is charged.</param>
public sealed record GuestBookingView(
    string BookingCode,
    GuestBookingStatus Status,
    PaymentOption PaymentOption,
    Guid PropertyId,
    string? PropertySlug,
    string PropertyName,
    string? PropertyCity,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    int NumberOfAdults,
    int NumberOfChildren,
    decimal Lodging,
    decimal CleaningFee,
    decimal TouristTax,
    decimal TotalPrice,
    decimal PaidAmount,
    decimal RefundedAmount,
    string Currency,
    DateTime? ExpiresAt,
    DateOnly? DeferredChargeDate,
    GuestBookingHostContact Host,
    GuestCheckInAccess CheckIn);

public sealed record GuestBookingHostContact(string? Name, string? Email);

public sealed record GuestCheckInAccess(GuestCheckInAccessStatus Status, DateOnly? OpensOn, DateTime? LinkSentAt);

public enum GuestCheckInAccessStatus
{
    NotApplicable,

    NotYetOpen,

    Open,

    /// <summary>The guests' data were sent, or the host already sent the Alloggiati Web communication.</summary>
    Completed,
}

public enum GuestBookingStatus
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

    StayInProgress,

    StayCompleted,
}

public static class GuestBookingLookupErrorCodes
{
    public const string NotFound = "guest_booking_not_found";

    public const string CheckInLinkUnavailable = "guest_check_in_link_unavailable";
}

public static class GuestBookings
{
    /// <summary>
    /// What Stripe collected for <paramref name="booking"/> and what was refunded so far, in the currency of its property: the settled
    /// payments only (completed, partially refunded, refunded), as "Le mie prenotazioni" and the guest's account list show them.
    /// Needs its payments.
    /// </summary>
    public static (decimal Paid, decimal Refunded) AmountsOf(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        var collected = booking.Payments
            .Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
            .ToList();
        return (collected.Sum(p => p.Amount), collected.Sum(p => p.RefundedAmount));
    }

    public static GuestBookingStatus StatusOf(Booking booking, HoldExpiryCutoff cutoff)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.Status switch
        {
            BookingStatus.CheckedIn => GuestBookingStatus.StayInProgress,
            BookingStatus.CheckedOut => GuestBookingStatus.StayCompleted,
            _ => CheckoutOutcomes.StateOf(booking, cutoff) switch
            {
                CheckoutOutcomeState.Confirmed => GuestBookingStatus.Confirmed,
                CheckoutOutcomeState.AwaitingPayment => GuestBookingStatus.AwaitingPayment,
                CheckoutOutcomeState.PaymentProcessing => GuestBookingStatus.PaymentProcessing,
                CheckoutOutcomeState.PaymentFailed => GuestBookingStatus.PaymentFailed,
                CheckoutOutcomeState.AwaitingGuestEmail => GuestBookingStatus.AwaitingGuestEmail,
                CheckoutOutcomeState.AwaitingHostApproval => GuestBookingStatus.AwaitingHostApproval,
                CheckoutOutcomeState.Expired => GuestBookingStatus.Expired,
                CheckoutOutcomeState.Declined => GuestBookingStatus.Declined,
                CheckoutOutcomeState.DatesUnavailable => GuestBookingStatus.DatesUnavailable,
                CheckoutOutcomeState.Cancelled => GuestBookingStatus.Cancelled,
                var other => throw new ArgumentOutOfRangeException(nameof(booking), other, "Unknown checkout state."),
            },
        };
    }
}
