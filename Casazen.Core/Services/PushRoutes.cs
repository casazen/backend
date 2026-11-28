using System.Text.RegularExpressions;

namespace Casazen.Core.Services;

public static partial class PushRoutes
{
    public const string Properties = "/properties";

    public static string Booking(Guid bookingId) => $"/bookings/{bookingId}";

    public static string BookingCheckout(Guid bookingId) => $"/bookings/{bookingId}/checkout";

    public static string BookingMessages(Guid bookingId) => $"/bookings/{bookingId}/messages";

    public static string MessageThread(string threadKey)
    {
        if (string.IsNullOrEmpty(threadKey) || !ThreadKeyPattern().IsMatch(threadKey))
            throw new ArgumentException("A thread key is 'ota:' or 'direct:' followed by a GUID.", nameof(threadKey));

        return $"/messages/{threadKey}";
    }

    public static string Approvals() => "/approvals";

    public static string SupplierRequest(Guid serviceRequestId) => $"/supplier/requests/{serviceRequestId}";

    public static bool IsAppRoute(string? route) => route is not null && AppRoutePattern().IsMatch(route);

    [GeneratedRegex(
        "^(/properties|/approvals|/bookings/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(/checkout|/messages)?|/messages/(ota|direct):[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|/supplier/requests/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AppRoutePattern();

    [GeneratedRegex("^(ota|direct):[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreadKeyPattern();
}
