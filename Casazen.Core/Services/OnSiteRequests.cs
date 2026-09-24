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
/// While pending the request holds its dates on the booking site and in the host calendar, but is not exported to the
/// OTAs through iCal (<see cref="IsExportedToOtas"/>).
/// </summary>
public static class OnSiteRequests
{
    public const string ApprovalHoursSetting = "DirectBooking:OnSiteApprovalHours";
    public const string EmailVerificationMinutesSetting = "DirectBooking:OnSiteEmailVerificationMinutes";
    public const string MaxNightsSetting = "DirectBooking:OnSiteMaxNights";

    /// <summary>
    /// PROVISIONAL technical default (not a product rule, BK-06 DUBBI): how long the host has to answer, so that no request
    /// holds its dates forever. The product owner decides the real value (<c>docs/runbooks/direct-booking.md</c>).
    /// </summary>
    public const int ProvisionalApprovalHours = 24;

    /// <summary>
    /// PROVISIONAL technical default (anti-abuse, not a product rule, BK-06 DUBBI): the longest stay a "pay at the property"
    /// request may ask for. Aligned with the "locazione breve" of art. 4 D.L. 50/2017 (contracts up to 30 days,
    /// <c>.claude/context/regulations/fiscale.md</c> C1); the product owner decides the real value.
    /// </summary>
    public const int ProvisionalMaxNights = 30;

    /// <summary>Hours the host has to accept or decline a request once the guest confirmed the email (at least 1).</summary>
    public static int GetApprovalHours(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(ApprovalHoursSetting, ProvisionalApprovalHours));

    /// <summary>
    /// Minutes the guest has to confirm the email (at least 1). Default: the checkout TTL
    /// (<see cref="CheckoutHolds.GetTtlMinutes"/>), like the time given to pay online.
    /// </summary>
    public static int GetEmailVerificationMinutes(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(EmailVerificationMinutesSetting, CheckoutHolds.GetTtlMinutes(configuration)));

    /// <summary>Longest stay of a request, in nights (at least 1).</summary>
    public static int GetMaxNights(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(MaxNightsSetting, ProvisionalMaxNights));

    /// <summary>A "pay at the property" request still pending (whatever its deadline).</summary>
    public static bool IsOpenRequest(Booking booking) =>
        booking.Status == BookingStatus.Pending &&
        booking.Source == BookingSource.Direct &&
        booking.PaymentOption == PaymentOption.OnSite;

    /// <summary>Where a request stands at <paramref name="nowUtc"/>; <c>null</c> when it is not an open request or its deadline has passed.</summary>
    public static OnSiteRequestState? StateOf(Booking booking, DateTime nowUtc)
    {
        if (!IsOpenRequest(booking) || booking.RequestExpiresAt is not { } expiresAt || expiresAt < nowUtc)
            return null;

        return booking.GuestEmailVerifiedAt is null
            ? OnSiteRequestState.AwaitingGuestEmail
            : OnSiteRequestState.AwaitingHostApproval;
    }

    /// <summary>Requests the host can accept or decline at <paramref name="nowUtc"/>: email confirmed, deadline not passed.</summary>
    public static Expression<Func<Booking, bool>> IsAwaitingHostApproval(DateTime nowUtc) =>
        b => b.Status == BookingStatus.Pending &&
             b.Source == BookingSource.Direct &&
             b.PaymentOption == PaymentOption.OnSite &&
             b.GuestEmailVerifiedAt != null &&
             b.RequestExpiresAt != null &&
             b.RequestExpiresAt >= nowUtc;

    /// <summary>
    /// Bookings written to the iCal export read by the OTAs: every one except a pending "pay at the property" request. An
    /// anonymous request must not block Airbnb/Booking before the host has accepted it (A3-06); once accepted it is
    /// exported like any confirmed booking.
    /// </summary>
    public static Expression<Func<Booking, bool>> IsExportedToOtas() =>
        b => !(b.Status == BookingStatus.Pending &&
               b.Source == BookingSource.Direct &&
               b.PaymentOption == PaymentOption.OnSite);

    /// <summary>The reason recorded when an expired request is cancelled.</summary>
    public static BookingCancellationReason ExpiryReason(Booking booking) =>
        booking.GuestEmailVerifiedAt is null
            ? BookingCancellationReason.OnSiteEmailNotConfirmed
            : BookingCancellationReason.OnSiteRequestExpired;

    /// <summary>New random token for the email confirmation link (URL-safe, 256 bits).</summary>
    public static string NewEmailVerificationToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>What is stored for a token (<see cref="Booking.GuestEmailVerificationTokenHash"/>): SHA-256, lowercase hex.</summary>
    public static string HashEmailVerificationToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Constant-time comparison of a token from a link with the stored hash.</summary>
    public static bool EmailVerificationTokenMatches(string? storedHash, string? token)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return false;

        var actual = Encoding.ASCII.GetBytes(HashEmailVerificationToken(token.Trim()));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Where an open "pay at the property" request stands (<see cref="OnSiteRequests.StateOf"/>).</summary>
public enum OnSiteRequestState
{
    /// <summary>Waiting for the guest to confirm the email; the host does not see it yet.</summary>
    AwaitingGuestEmail,

    /// <summary>Email confirmed: waiting for the host to accept or decline.</summary>
    AwaitingHostApproval,
}
