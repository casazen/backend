namespace Casazen.Core.OfficialData;

/// <summary>
/// Hosts the official-data job may call: ISTAT, the Alloggiati portal, <c>*.gov.it</c> and institutional
/// <c>comune.*.it</c> sites. Anything else is refused (no scraping of third-party aggregators).
/// </summary>
public static class OfficialHostAllowlist
{
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host is "www.istat.it" or "istat.it" or "situas.istat.it" or "situas-servizi.istat.it")
            return true;
        if (host is "alloggiatiweb.poliziadistato.it")
            return true;
        if (host.EndsWith(".gov.it", StringComparison.Ordinal) || host == "gov.it")
            return true;
        if (host.Contains(".comune.", StringComparison.Ordinal) && host.EndsWith(".it", StringComparison.Ordinal))
            return true;
        if (host.StartsWith("comune.", StringComparison.Ordinal) && host.EndsWith(".it", StringComparison.Ordinal))
            return true;
        return false;
    }

    public static bool TryCreateAllowed(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (!IsAllowed(parsed))
            return false;
        uri = parsed;
        return true;
    }
}
