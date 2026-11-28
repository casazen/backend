namespace Casazen.Core.Options;

public class ComplianceArchiveOptions
{
    /// <summary>The portal of Alloggiati Web (Polizia di Stato) where the host gets the receipt of a communication (the guided step of NF-43).</summary>
    public const string DefaultAlloggiatiPortalUrl = "https://alloggiatiweb.poliziadistato.it/";

    public const int MaxSignedUrlSeconds = 900;

    public const int MaxUploadMegabytes = 25;

    /// <summary>
    /// The address of the Alloggiati Web portal the archive page shows next to «Get the receipt». Only an <c>https</c> address without
    /// credentials is used; anything else falls back to <see cref="DefaultAlloggiatiPortalUrl"/>, so a typo in the variable never puts a
    /// strange link in front of a host.
    /// </summary>
    public string AlloggiatiPortalUrl { get; set; } = DefaultAlloggiatiPortalUrl;

    public int SignedUrlSeconds { get; set; } = 120;

    public int UploadMaxMegabytes { get; set; } = 10;

    public int ExportMaxFiles { get; set; } = 500;

    public int ExportMaxMegabytes { get; set; } = 50;

    public TimeSpan SignedUrlLifetime => TimeSpan.FromSeconds(SignedUrlSeconds is >= 1 and <= MaxSignedUrlSeconds ? SignedUrlSeconds : 120);

    public long UploadMaxBytes => (UploadMaxMegabytes is >= 1 and <= MaxUploadMegabytes ? UploadMaxMegabytes : 10) * 1024L * 1024L;

    public int ExportFileLimit => ExportMaxFiles is >= 1 and <= 2000 ? ExportMaxFiles : 500;

    public long ExportByteLimit => (ExportMaxMegabytes is >= 1 and <= 200 ? ExportMaxMegabytes : 50) * 1024L * 1024L;

    /// <summary>The portal address to show: the configured one when it is <c>https</c> without credentials, otherwise the default.</summary>
    public string PortalUrl =>
        Uri.TryCreate(AlloggiatiPortalUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : DefaultAlloggiatiPortalUrl;
}
