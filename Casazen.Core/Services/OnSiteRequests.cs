using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

/// <summary>
/// "Pay at the property" requests (<see cref="PaymentOption.OnSite"/>, decision D5, BK-06, A3-06): never confirmed by the
/// checkout. The booking stays <see cref="BookingStatus.Pending"/>:
/// <list type="number">
/// <item>the guest confirms the email address through the link of the "request received" email, within
/// <c>DirectBooking:OnSiteEmailVerificationMinutes</c> (default: the checkout TTL);</item>
/// <item>the request then goes to the host, who accepts it (Confirmed, valid) or declines it (Cancelled) within
/// <c>DirectBooking:OnSiteApprovalHours</c>;</item>
/// <item>past either deadline (<see cref="Booking.RequestExpiresAt"/>) the <c>checkout-hold-expiry</c> job cancels it
/// (<see cref="CheckoutHolds.IsExpired"/>).</item>
/// </list>
/// While pending the request holds its dates on the booking site and in the host calendar, and is also exported to the
/// OTAs through iCal (<see cref="IsExportedToOtas"/>) so that those dates are blocked on external channels (BK-06).
/// </summary>
public static class OnSiteRequests
{
    public const string ApprovalHoursSetting = "DirectBooking:OnSiteApprovalHours";
    public const string EmailVerificationMinutesSetting = "DirectBooking:OnSiteEmailVerificationMinutes";
    public const string MaxNightsSetting = "DirectBooking:OnSiteMaxNights";

    public const int DefaultApprovalHours = 24;

    public const int ProvisionalApprovalHours = DefaultApprovalHours;

    public const int DefaultMaxNights = 30;

    public const int ProvisionalMaxNights = DefaultMaxNights;

    public static int GetApprovalHours(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(ApprovalHoursSetting, DefaultApprovalHours));

    public static int GetEmailVerificationMinutes(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(EmailVerificationMinutesSetting, CheckoutHolds.GetTtlMinutes(configuration)));

    public static int GetMaxNights(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(MaxNightsSetting, DefaultMaxNights));

    public static bool IsOpenRequest(Booking booking) =>
        booking.Status == BookingStatus.Pending &&
        booking.Source == BookingSource.Direct &&
        booking.PaymentOption == PaymentOption.OnSite;

    public static OnSiteRequestState? StateOf(Booking booking, DateTime nowUtc)
    {
        if (!IsOpenRequest(booking) || booking.RequestExpiresAt is not { } expiresAt || expiresAt < nowUtc)
            return null;

        return booking.GuestEmailVerifiedAt is null
            ? OnSiteRequestState.AwaitingGuestEmail
            : OnSiteRequestState.AwaitingHostApproval;
    }

    public static Expression<Func<Booking, bool>> IsAwaitingHostApproval(DateTime nowUtc) =>
        b => b.Status == BookingStatus.Pending &&
             b.Source == BookingSource.Direct &&
             b.PaymentOption == PaymentOption.OnSite &&
             b.GuestEmailVerifiedAt != null &&
             b.RequestExpiresAt != null &&
             b.RequestExpiresAt >= nowUtc;

    /// <summary>
    /// Bookings written to the iCal export read by the OTAs: all bookings, including pending "pay at the property"
    /// requests (BK-06). Pending OnSite requests must block the OTAs from the moment they are created so that the
    /// host's calendar on Airbnb/Booking is kept in sync; if the request is later declined or expires, the block
    /// is lifted by the cancellation removing it from the export.
    /// </summary>
    public static Expression<Func<Booking, bool>> IsExportedToOtas() =>
        b => true;

    public static BookingCancellationReason ExpiryReason(Booking booking) =>
        booking.GuestEmailVerifiedAt is null
            ? BookingCancellationReason.OnSiteEmailNotConfirmed
            : BookingCancellationReason.OnSiteRequestExpired;

    public static string NewEmailVerificationToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string HashEmailVerificationToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool EmailVerificationTokenMatches(string? storedHash, string? token)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return false;

        var actual = Encoding.ASCII.GetBytes(HashEmailVerificationToken(token.Trim()));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public enum OnSiteRequestState
{
    AwaitingGuestEmail,

    AwaitingHostApproval,
}
