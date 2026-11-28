namespace Casazen.Web.DTOs.Admin;

public class ClientIpDiagnosticsDto
{
    public string? ClientIp { get; set; }

    public string RateLimitKey { get; set; } = string.Empty;

    public string? OriginalPeer { get; set; }

    public IReadOnlyList<string> UnprocessedForwardedFor { get; set; } = [];

    public string Scheme { get; set; } = string.Empty;

    public int? ForwardLimit { get; set; }

    public IReadOnlyList<string> KnownNetworks { get; set; } = [];

    public IReadOnlyList<string> KnownProxies { get; set; } = [];
}
