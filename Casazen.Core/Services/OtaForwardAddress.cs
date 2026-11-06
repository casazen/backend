using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Whether an e-mail address is one an online travel agency forwards a guest's mail through (E07/F01/US02/T01, NF-88, NF-89, AC-NF89-5): the
/// guest does not own it, it works for the stay and then goes quiet, so it is never a key to recognise a person by and never an address to
/// write to for marketing. Two independent rules, either is enough:
/// <list type="bullet">
/// <item><b>The origin</b>: the booking came from a channel (<see cref="IsOtaSource"/>). This does not depend on any list, so it holds for a
/// relay domain nobody has seen yet.</item>
/// <item><b>The domain</b>: the host of the address is one of the relay domains the product owner confirmed against real bookings
/// (<c>Crm:OtaForwardDomains</c>), or a subdomain of one; the list is empty until a domain is seen in a real booking. A false positive only
/// keeps an address out of marketing, which is the safe side of the mistake, so a subdomain counts.</item>
/// </list>
/// </summary>
public static class OtaForwardAddress
{
    /// <summary>True for the sources that are an online travel agency: Airbnb, Booking.com, Expedia, Vrbo, TripAdvisor and Agoda.</summary>
    public static bool IsOtaSource(BookingSource? source) =>
        source is BookingSource.Airbnb
            or BookingSource.BookingCom
            or BookingSource.Expedia
            or BookingSource.Vrbo
            or BookingSource.TripAdvisor
            or BookingSource.Agoda;

    /// <summary>
    /// True when <paramref name="bookingSource"/> is an online travel agency or the host of <paramref name="email"/> is one of
    /// <paramref name="relayDomains"/>. A booking with no known source (<c>null</c>) is judged by the domain alone.
    /// </summary>
    public static bool IsForward(string? email, BookingSource? bookingSource, IReadOnlyCollection<string>? relayDomains) =>
        IsOtaSource(bookingSource) || IsOnRelayDomain(email, relayDomains);

    /// <summary>
    /// True when the host of <paramref name="email"/> is one of <paramref name="relayDomains"/> or a subdomain of one (<c>reply.guest.example.com</c>
    /// is on <c>guest.example.com</c>). Case and a trailing dot do not matter; an empty list, an address with no host and a blank entry match nothing.
    /// </summary>
    public static bool IsOnRelayDomain(string? email, IReadOnlyCollection<string>? relayDomains)
    {
        if (relayDomains is null || relayDomains.Count == 0 || string.IsNullOrWhiteSpace(email))
            return false;

        var address = email.Trim();
        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1)
            return false;

        var host = address[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
            return false;

        foreach (var domain in relayDomains)
        {
            var relay = domain?.Trim().Trim('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(relay))
                continue;

            if (host == relay || host.EndsWith("." + relay, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
