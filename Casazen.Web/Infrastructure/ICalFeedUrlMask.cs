namespace Casazen.Web.Infrastructure;

/// <summary>
/// Masked form of an iCal import URL for the API (PC-11, A2-20): the host and the last four characters, enough for
/// the host to recognize the link, never the token it carries (Airbnb: <c>?s=…</c>).
/// </summary>
public static class ICalFeedUrlMask
{
    private const int VisibleTail = 4;

    // Shorter paths show no tail: four characters would be most of them.
    private const int MinPathForTail = 12;

    /// <summary><c>https://www.airbnb.it/calendar/ical/123.ics?s=ab12cd34</c> → <c>www.airbnb.it/…cd34</c>; null when there is no URL.</summary>
    public static string? Mask(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return null;

        var rest = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimEnd('/');
        return rest.Length >= MinPathForTail
            ? $"{uri.Host}/…{rest[^VisibleTail..]}"
            : $"{uri.Host}/…";
    }
}
