using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// "Le mie prenotazioni" of a booking site (BK-11, A3-10, R-06): the guest finds one booking of that site with its booking
/// code and the email the booking was made with. A code that does not exist, a code of another site and an email that
/// does not match get the same <see cref="Exceptions.NotFoundException"/> (<see cref="GuestBookingLookupErrorCodes.NotFound"/>),
/// so the answer never tells which bookings exist. The endpoints are rate limited per client IP and per email.
/// </summary>
public interface IGuestBookingLookupService
{
    /// <summary>The booking of <paramref name="credentials"/>, as its guest sees it.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>.</exception>
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

/// <summary>What the guest types: the site (org slug), the booking code as written in the email, the email address.</summary>
public sealed record GuestBookingCredentials(string OrgSlug, string BookingCode, string Email);

/// <summary>
/// One booking as its guest sees it in "Le mie prenotazioni": state, stay, amounts in euro as recorded on the booking,
/// the host's public contact and the online check-in. No personal data of anyone (the guest typed their own email).
/// </summary>
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

/// <summary>
/// The host's contact of the booking site (the name and email of the site footer and of the confirmation email, BK-10).
/// Either may be empty.
/// </summary>
public sealed record GuestBookingHostContact(string? Name, string? Email);

/// <summary>Where the online check-in of the booking stands for its guest.</summary>
/// <param name="OpensOn">
/// <see cref="GuestCheckInAccessStatus.NotYetOpen"/>: the day the link is emailed (check-in minus <c>CheckIn:SendWindowDays</c>).
/// </param>
/// <param name="LinkSentAt">When the email of the link that still works was handed to the email provider, if any (CO-09).</param>
public sealed record GuestCheckInAccess(GuestCheckInAccessStatus Status, DateOnly? OpensOn, DateTime? LinkSentAt);

/// <summary>Online check-in of a booking as its guest sees it. Serialized by name.</summary>
public enum GuestCheckInAccessStatus
{
    /// <summary>No online check-in: the booking is not confirmed, or the stay is over.</summary>
    NotApplicable,

    /// <summary>Confirmed, arrival still far: the link is emailed on <see cref="GuestCheckInAccess.OpensOn"/>.</summary>
    NotYetOpen,

    /// <summary>The guest can fill in the online check-in: the link is (or can be sent again) in their mailbox.</summary>
    Open,

    /// <summary>The guests' data were sent, or the host already sent the Alloggiati Web communication.</summary>
    Completed,
}

/// <summary>
/// State of a booking for its guest (<see cref="GuestBookings.StatusOf"/>): the states of the checkout outcome page
/// (BK-07), plus the stay in progress and over. Serialized by name.
/// </summary>
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

    /// <summary>The host recorded the arrival.</summary>
    StayInProgress,

    /// <summary>The host recorded the departure.</summary>
    StayCompleted,
}

/// <summary>Stable error codes of "Le mie prenotazioni" (frontend <c>apiErrors.codes.*</c>).</summary>
public static class GuestBookingLookupErrorCodes
{
    /// <summary>404: no booking of this site matches code and email (the same answer whatever does not match).</summary>
    public const string NotFound = "guest_booking_not_found";

    /// <summary>409: the online check-in link cannot be sent now (not open yet, completed, booking not confirmed).</summary>
    public const string CheckInLinkUnavailable = "guest_check_in_link_unavailable";
}

/// <summary>The state of a booking for its guest, from the same rules as the checkout outcome page.</summary>
public static class GuestBookings
{
    /// <summary>Where <paramref name="booking"/> stands at <paramref name="cutoff"/>. Needs its payments.</summary>
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
