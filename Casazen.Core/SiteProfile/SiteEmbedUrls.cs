using System.Text.RegularExpressions;

namespace Casazen.Core.SiteProfile;

public enum SiteEmbedProvider
{
    YouTube = 0,

    Vimeo = 1,

    Matterport = 2,
}

public sealed record SiteEmbed(string Url, SiteEmbedProvider Provider);

/// <summary>
/// The addresses of videos and tours (DB-11a, DB-D17). A house can carry one video (YouTube or Vimeo) and one 360° tour (Matterport),
/// given as an address; nothing is uploaded. The host pastes the address they copy from the provider (a page address, a short link, an
/// embed address) and the system keeps only the <b>canonical embed address</b> built from the identifier it recognises: so the stored
/// value is always on the allow-list of hosts, can carry nothing else (no query that autoplays, no fragment, no credentials, no other
/// scheme), and the page frame only has to allow those three hosts.
/// </summary>
public static partial class SiteEmbedUrls
{
    private const string YouTubeEmbed = "https://www.youtube-nocookie.com/embed/";
    private const string VimeoEmbed = "https://player.vimeo.com/video/";
    private const string MatterportShow = "https://my.matterport.com/show/?m=";

    public static IReadOnlyList<string> FrameHosts { get; } =
        ["https://www.youtube-nocookie.com", "https://player.vimeo.com", "https://my.matterport.com"];

    public static SiteEmbed? NormalizeVideo(string? address) =>
        TryParse(address) is { } uri ? FromYouTube(uri) ?? FromVimeo(uri) : null;

    /// <summary>The canonical embed of a tour address (Matterport); <c>null</c> otherwise.</summary>
    public static SiteEmbed? NormalizeTour(string? address) =>
        TryParse(address) is { } uri ? FromMatterport(uri) : null;

    public static SiteEmbed? Recognize(string? stored) => NormalizeVideo(stored) ?? NormalizeTour(stored);

    private static Uri? TryParse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > SitePropertyProfileLimits.EmbedUrlMaxLength)
            return null;

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            return null;

        return uri;
    }

    private static SiteEmbed? FromYouTube(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        string? id = null;

        if (host is "youtu.be")
        {
            id = FirstSegment(uri);
        }
        else if (host is "www.youtube.com" or "youtube.com" or "m.youtube.com" or "www.youtube-nocookie.com" or "youtube-nocookie.com")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is ["embed" or "shorts" or "live" or "v", var second])
                id = second;
            else if (segments is ["watch"])
                id = QueryValue(uri, "v");
        }

        return id is not null && YouTubeId().IsMatch(id) ? new SiteEmbed(YouTubeEmbed + id, SiteEmbedProvider.YouTube) : null;
    }

    private static SiteEmbed? FromVimeo(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("vimeo.com" or "www.vimeo.com" or "player.vimeo.com"))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        string? hash = null;
        if (host == "player.vimeo.com")
        {
            if (segments is ["video", var videoId])
            {
                id = videoId;
                hash = QueryValue(uri, "h");
            }
        }
        else if (segments is [var plainId])
        {
            id = plainId;
        }
        else if (segments is [var unlistedId, var unlistedHash])
        {
            id = unlistedId;
            hash = unlistedHash;
        }

        if (id is null || !VimeoId().IsMatch(id))
            return null;

        if (hash is not null && !VimeoHash().IsMatch(hash))
            return null;

        return new SiteEmbed(VimeoEmbed + id + (hash is null ? string.Empty : "?h=" + hash), SiteEmbedProvider.Vimeo);
    }

    private static SiteEmbed? FromMatterport(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("my.matterport.com" or "matterport.com" or "www.matterport.com"))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (host == "my.matterport.com" && segments is ["show"])
            id = QueryValue(uri, "m");
        else if (host != "my.matterport.com" && segments is ["discover", "space", var spaceId])
            id = spaceId;

        return id is not null && MatterportId().IsMatch(id) ? new SiteEmbed(MatterportShow + id, SiteEmbedProvider.Matterport) : null;
    }

    private static string? FirstSegment(Uri uri) =>
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : null;

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(pair[..separator], name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex YouTubeId();

    [GeneratedRegex("^[0-9]{6,12}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VimeoId();

    [GeneratedRegex("^[0-9a-f]{6,20}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VimeoHash();

    [GeneratedRegex("^[A-Za-z0-9]{11}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MatterportId();
}
