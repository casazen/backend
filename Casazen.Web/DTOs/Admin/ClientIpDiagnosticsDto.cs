namespace Casazen.Web.DTOs.Admin;

/// <summary>How the API sees the caller behind the proxy chain (runbook <c>docs/runbooks/proxy-ip.md</c>).</summary>
public class ClientIpDiagnosticsDto
{
    /// <summary>Client IP used for consent evidence and rate limiting (<c>Connection.RemoteIpAddress</c>).</summary>
    public string? ClientIp { get; set; }

    /// <summary>Rate limiting partition of the client (IPv4 address or IPv6 /64).</summary>
    public string RateLimitKey { get; set; } = string.Empty;

    /// <summary>TCP peer before <c>X-Forwarded-For</c> was applied (<c>X-Original-For</c>); null when nothing was applied.</summary>
    public string? OriginalPeer { get; set; }

    /// <summary><c>X-Forwarded-For</c> entries left unprocessed (not trusted or beyond <c>ForwardLimit</c>), left to right.</summary>
    public IReadOnlyList<string> UnprocessedForwardedFor { get; set; } = [];

    /// <summary>Request scheme after <c>X-Forwarded-Proto</c>.</summary>
    public string Scheme { get; set; } = string.Empty;

    public int? ForwardLimit { get; set; }

    public IReadOnlyList<string> KnownNetworks { get; set; } = [];

    public IReadOnlyList<string> KnownProxies { get; set; } = [];
}
