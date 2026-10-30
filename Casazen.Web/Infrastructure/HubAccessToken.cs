namespace Casazen.Web.Infrastructure;

/// <summary>
/// The JWT of a SignalR connection. WebSockets and server-sent events cannot set an <c>Authorization</c> header in a browser, so the
/// SignalR client sends the token as <c>?access_token=</c>. It is read for the paths under <c>/hubs</c> only: on the API a token in
/// a URL would end up in proxy logs, so there it is ignored.
/// </summary>
public static class HubAccessToken
{
    public const string HubsPath = "/hubs";

    public const string QueryParameter = "access_token";

    public static bool TryRead(HttpRequest request, out string token)
    {
        ArgumentNullException.ThrowIfNull(request);
        token = string.Empty;
        if (!request.Path.StartsWithSegments(HubsPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = request.Query[QueryParameter].ToString();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        token = candidate;
        return true;
    }
}
