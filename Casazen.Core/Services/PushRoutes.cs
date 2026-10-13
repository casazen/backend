using System.Text.RegularExpressions;

namespace Casazen.Core.Services;

/// <summary>
/// Screens of the CasaZen Host app (mobile, expo-router) that a push notification can open when it is tapped (MO-03,
/// A6-19). The app opens only these routes (<c>mobile/src/notifications/notification-routes.ts</c>): keep the two
/// lists in sync, and build every <see cref="PushNotificationPayload.Route"/> with these helpers.
/// </summary>
public static partial class PushRoutes
{
    /// <summary>Property list tab: destination of a push about a property without a stay (e.g. a long-rent request).</summary>
    public const string Properties = "/properties";

    /// <summary>Booking detail.</summary>
    public static string Booking(Guid bookingId) => $"/bookings/{bookingId}";

    /// <summary>Quick check-out of a booking.</summary>
    public static string BookingCheckout(Guid bookingId) => $"/bookings/{bookingId}/checkout";

    /// <summary>True when <paramref name="route"/> is a screen the app can open from a push.</summary>
    public static bool IsAppRoute(string? route) => route is not null && AppRoutePattern().IsMatch(route);

    [GeneratedRegex(
        "^(/properties|/bookings/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(/checkout)?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AppRoutePattern();
}
