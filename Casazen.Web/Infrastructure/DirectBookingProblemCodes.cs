namespace Casazen.Web.Infrastructure;

/// <summary>
/// Stable <c>code</c> values of the public checkout errors (<c>POST /api/public/bookings</c>, BK-06 / R-11). The frontend
/// translates them (<c>apiErrors.codes.*</c>) instead of showing a generic "checkout failed". Dates and guests reuse the
/// booking codes (<see cref="Core.Services.BookingErrorCodes"/>); "pay at the property" requests have theirs in
/// <see cref="Core.Services.OnSiteRequestErrorCodes"/>.
/// </summary>
public static class DirectBookingProblemCodes
{
    /// <summary>409: the host has not completed Stripe Connect: the site does not take bookings yet (R-11).</summary>
    public const string PaymentsNotReady = "direct_booking_payments_not_ready";

    /// <summary>422: check-in in the past, or check-out not after check-in.</summary>
    public const string InvalidStay = "direct_booking_invalid_stay";

    /// <summary>400: the data processing consent was not given.</summary>
    public const string ConsentRequired = "direct_booking_consent_required";

    /// <summary>422: the consent text changed since the page was loaded.</summary>
    public const string ConsentOutdated = "direct_booking_consent_outdated";

    /// <summary>422: unknown payment option.</summary>
    public const string InvalidPaymentOption = "direct_booking_invalid_payment_option";
}
