using Casazen.Core.Identity;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The coarse family of the browser of a request, from its <c>User-Agent</c> header (E70/F02/US01/T01, NF-188): a code of <see cref="IdentityChannels"/>, the
/// channel axis of a segment (property × format × channel, NF-194) and of the coverage of the counters (NF-196). <b>The User-Agent itself is never stored, logged
/// or returned</b>: the caller hands it in and keeps only the code this answers. A pure function: no state, no I/O, no dependency.
/// </summary>
/// <remarks>
/// <para>The rules, in this order, on the first <see cref="MaxReadLength"/> characters of the header (tokens compared without regard to case):</para>
/// <list type="number">
/// <item><description><c>iPhone</c>, <c>iPad</c> or <c>iPod</c> with <c>AppleWebKit</c>: <see cref="IdentityChannels.IosSafari"/>, every browser of iOS included
/// (Chrome <c>CriOS</c>, Firefox <c>FxiOS</c>, Edge <c>EdgiOS</c> and the in-app views run on WebKit there). An iPad that asks for the desktop site sends the header
/// of a Mac and reads as <see cref="IdentityChannels.DesktopSafari"/>: the header cannot tell them apart.</description></item>
/// <item><description><c>Android</c>: <see cref="IdentityChannels.AndroidChrome"/> for Chrome itself (<c>Chrome/</c> and none of the tokens of
/// <see cref="NotAndroidChrome"/>: the other browsers built on Chromium, a WebView <c>; wv)</c>, an automated browser); every other browser of Android is
/// <see cref="IdentityChannels.Other"/>.</description></item>
/// <item><description>A desktop system (<c>Windows NT</c>, <c>Macintosh</c>, <c>X11</c>, <c>CrOS</c>) without a <c>Mobile</c> token: <c>Firefox/</c> is
/// <see cref="IdentityChannels.DesktopFirefox"/>; <c>Chrome/</c>, <c>Chromium/</c> or <c>Edg/</c> (Chrome, Edge, Opera, Brave, Vivaldi) is
/// <see cref="IdentityChannels.DesktopChromium"/>, unless a token of <see cref="NotDesktopChromium"/> says otherwise (the old Edge, an automated browser, an
/// application); <c>Version/</c> with <c>Safari/</c> on a Mac is <see cref="IdentityChannels.DesktopSafari"/>.</description></item>
/// <item><description>Anything else, no header at all, a bot, a television: <see cref="IdentityChannels.Other"/>.</description></item>
/// </list>
/// <para>Prudent by construction: a browser that is not recognised with certainty is <see cref="IdentityChannels.Other"/>, a channel no qualification names by default,
/// so a doubtful header never lands in a qualified segment.</para>
/// </remarks>
public static class IdentityChannelClassifier
{
    public const int MaxReadLength = 1024;

    private static readonly string[] NotAndroidChrome =
    [
        "; wv)", "Version/", "SamsungBrowser/", "EdgA/", "OPR/", "Opera", "YaBrowser/", "UCBrowser/", "MiuiBrowser/", "HuaweiBrowser/", "Vivaldi", "DuckDuckGo/",
        "Firefox/", "HeadlessChrome",
    ];

    private static readonly string[] NotDesktopChromium = ["Edge/", "HeadlessChrome", "Electron/"];

    private static readonly string[] DesktopSystems = ["Windows NT", "Macintosh", "X11", "CrOS"];

    private static readonly string[] IosDevices = ["iPhone", "iPad", "iPod"];

    public static string Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return IdentityChannels.Other;

        var header = userAgent.Length > MaxReadLength ? userAgent[..MaxReadLength] : userAgent;

        if (HasAny(header, IosDevices))
            return Has(header, "AppleWebKit") ? IdentityChannels.IosSafari : IdentityChannels.Other;

        if (Has(header, "Android"))
            return Has(header, "Chrome/") && !HasAny(header, NotAndroidChrome) ? IdentityChannels.AndroidChrome : IdentityChannels.Other;

        if (!HasAny(header, DesktopSystems) || Has(header, "Mobile"))
            return IdentityChannels.Other;

        if (Has(header, "Firefox/"))
            return IdentityChannels.DesktopFirefox;

        if (Has(header, "Chrome/") || Has(header, "Chromium/") || Has(header, "Edg/"))
            return HasAny(header, NotDesktopChromium) ? IdentityChannels.Other : IdentityChannels.DesktopChromium;

        if (Has(header, "Macintosh") && Has(header, "Version/") && Has(header, "Safari/"))
            return IdentityChannels.DesktopSafari;

        return IdentityChannels.Other;
    }

    private static bool Has(string header, string token)
    {
        return header.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAny(string header, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (Has(header, token))
                return true;
        }

        return false;
    }
}
