using Casazen.Core.Services;

namespace Casazen.Core.Options;

/// <summary>
/// The <c>Octorate:ComplianceHandoff</c> section (E03/F05/US01/T01, CHK-15): where the guided check in the Octorate Cassetto Compliance
/// sends the host. Every address comes from configuration, never from code and never from a caller; nothing here is required, because
/// without a verified deep link the handoff opens the authenticated home of the panel (<c>Octorate:FrontendBaseUrl</c>) and the guide
/// describes the menu steps.
/// </summary>
/// <remarks>
/// <para><b>Allowlist.</b> A destination is used only when it is an absolute <c>https</c> address of a named host (no IP address), without
/// credentials and with the default port, on a host that is the host of <c>Octorate:FrontendBaseUrl</c> or one of
/// <see cref="AllowedHosts"/>. Anything else is ignored: a deep link that fails the check is replaced by the home (the caller logs it), and
/// with no valid home either there is no destination (the service answers 409).</para>
/// <para><b>Verified.</b> A deep link is used only while its <c>Verified</c> is on, which a person sets after opening it in the real
/// panel. The Octorate parameters of a booking, a guest or a period are not documented, so none is ever added to an address: a link is
/// used exactly as configured.</para>
/// </remarks>
public sealed class OctorateComplianceHandoffOptions
{
    public const string SectionName = "Octorate:ComplianceHandoff";

    /// <summary>Minutes a handoff id is valid when the setting is not given.</summary>
    public const int DefaultLifetimeMinutes = 60;

    /// <summary>The longest life a handoff id may be given.</summary>
    public const int MaxLifetimeMinutes = 1440;

    /// <summary>Minutes the client may keep the context of a handoff (1 to <see cref="MaxLifetimeMinutes"/>; out of range: the default).</summary>
    public int LifetimeMinutes { get; set; } = DefaultLifetimeMinutes;

    /// <summary>
    /// Host names a deep link may point to besides the host of <c>Octorate:FrontendBaseUrl</c>, separated by commas, semicolons or spaces
    /// (exact names, ASCII: an internationalized name is written in punycode). Empty: only the host of the panel.
    /// </summary>
    public string? AllowedHosts { get; set; }

    /// <summary>The deep link for the Alloggiati outcome.</summary>
    public OctorateHandoffLinkOptions Alloggiati { get; set; } = new();

    /// <summary>The deep link for the ISTAT outcome.</summary>
    public OctorateHandoffLinkOptions Istat { get; set; } = new();

    /// <summary>The life of a handoff id, brought into 1 to <see cref="MaxLifetimeMinutes"/> minutes.</summary>
    public TimeSpan Lifetime =>
        TimeSpan.FromMinutes(LifetimeMinutes is >= 1 and <= MaxLifetimeMinutes ? LifetimeMinutes : DefaultLifetimeMinutes);

    /// <summary>The deep link configured for <paramref name="topic"/>.</summary>
    public OctorateHandoffLinkOptions LinkOf(OctorateHandoffTopic topic) => topic switch
    {
        OctorateHandoffTopic.Alloggiati => Alloggiati,
        OctorateHandoffTopic.Istat => Istat,
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown handoff topic."),
    };

    /// <summary>
    /// The one address a handoff for <paramref name="topic"/> opens: the verified deep link when it passes the allowlist, else the home of
    /// the panel (<paramref name="panelBaseUrl"/>, <c>Octorate:FrontendBaseUrl</c>) when it passes it, else <c>null</c>.
    /// </summary>
    /// <param name="topic">What the host goes to check.</param>
    /// <param name="panelBaseUrl">The configured login and panel address of Octorate.</param>
    /// <param name="deepLinkRejected">True when a verified deep link was configured and failed the allowlist (the caller logs it, without the address).</param>
    public OctorateHandoffDestination? ResolveDestination(OctorateHandoffTopic topic, string? panelBaseUrl, out bool deepLinkRejected)
    {
        deepLinkRejected = false;

        var home = ParseHttps(panelBaseUrl);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (home is not null)
            hosts.Add(home.IdnHost);

        foreach (var entry in (AllowedHosts ?? string.Empty).Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.CheckHostName(entry) == UriHostNameType.Dns)
                hosts.Add(entry);
        }

        var link = LinkOf(topic);
        if (link.Verified)
        {
            var deepLink = ParseHttps(link.Url);
            if (deepLink is not null && hosts.Contains(deepLink.IdnHost))
                return new OctorateHandoffDestination(deepLink.AbsoluteUri, OctorateHandoffDestinationKind.DeepLink);

            deepLinkRejected = true;
        }

        return home is null ? null : new OctorateHandoffDestination(home.AbsoluteUri, OctorateHandoffDestinationKind.Home);
    }

    /// <summary>
    /// The address as a <see cref="Uri"/> when it is absolute <c>https</c> for a named host, with no credentials, no backslash, no space or
    /// control character, and the default port; otherwise <c>null</c>. What is returned is the parse the destination is built from, so the
    /// string the client opens is the one that was checked.
    /// </summary>
    private static Uri? ParseHttps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        foreach (var c in text)
        {
            if (c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c))
                return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        return uri.Scheme == Uri.UriSchemeHttps
               && uri.HostNameType == UriHostNameType.Dns
               && string.IsNullOrEmpty(uri.UserInfo)
               && uri.IsDefaultPort
            ? uri
            : null;
    }
}

/// <summary>A deep link of the Octorate panel for one topic, used only once a person verified it (<c>Verified</c>).</summary>
public sealed class OctorateHandoffLinkOptions
{
    /// <summary>The address, absolute https on an allowlisted host. Empty: the topic has no deep link.</summary>
    public string? Url { get; set; }

    /// <summary>A person opened the address in the real panel and it lands on the right section. Off: the address is ignored.</summary>
    public bool Verified { get; set; }
}
