namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Compliance:Archive</c> (E03/F02/US03/T01, NF-47, NF-52): the archive of the proofs CasaZen holds for the compliance
/// obligations, its uploads, its downloads and the audit exports. Nothing here is required: every value has a default.
/// </summary>
public class ComplianceArchiveOptions
{
    /// <summary>The portal of Alloggiati Web (Polizia di Stato) where the host gets the receipt of a communication (the guided step of NF-43).</summary>
    public const string DefaultAlloggiatiPortalUrl = "https://alloggiatiweb.poliziadistato.it/";

    /// <summary>Longest signed address of a download, in seconds.</summary>
    public const int MaxSignedUrlSeconds = 900;

    /// <summary>The largest value <see cref="UploadMaxMegabytes"/> may take: the request size limit of the upload endpoint is built from it.</summary>
    public const int MaxUploadMegabytes = 25;

    /// <summary>
    /// The address of the Alloggiati Web portal the archive page shows next to «Get the receipt». Only an <c>https</c> address without
    /// credentials is used; anything else falls back to <see cref="DefaultAlloggiatiPortalUrl"/>, so a typo in the variable never puts a
    /// strange link in front of a host.
    /// </summary>
    public string AlloggiatiPortalUrl { get; set; } = DefaultAlloggiatiPortalUrl;

    /// <summary>
    /// How long the signed address a download redirects to stays valid, in seconds (1 to <see cref="MaxSignedUrlSeconds"/>; out of range:
    /// 120). Short on purpose: the address is made again for every download, and nobody can keep it.
    /// </summary>
    public int SignedUrlSeconds { get; set; } = 120;

    /// <summary>The largest proof a host may upload, in megabytes (1 to <see cref="MaxUploadMegabytes"/>; out of range: 10).</summary>
    public int UploadMaxMegabytes { get; set; } = 10;

    /// <summary>
    /// The most proofs one export carries (1 to 2000; out of range: 500). The export is built on request and never stored, so it is bounded:
    /// beyond it the host asks for a shorter period.
    /// </summary>
    public int ExportMaxFiles { get; set; } = 500;

    /// <summary>The most bytes the files of one export add up to, in megabytes (1 to 200; out of range: 50).</summary>
    public int ExportMaxMegabytes { get; set; } = 50;

    /// <summary>The signed address lifetime, within its range.</summary>
    public TimeSpan SignedUrlLifetime => TimeSpan.FromSeconds(SignedUrlSeconds is >= 1 and <= MaxSignedUrlSeconds ? SignedUrlSeconds : 120);

    /// <summary>The largest upload in bytes, within its range.</summary>
    public long UploadMaxBytes => (UploadMaxMegabytes is >= 1 and <= MaxUploadMegabytes ? UploadMaxMegabytes : 10) * 1024L * 1024L;

    /// <summary>The most files of an export, within its range.</summary>
    public int ExportFileLimit => ExportMaxFiles is >= 1 and <= 2000 ? ExportMaxFiles : 500;

    /// <summary>The most bytes of an export, within its range.</summary>
    public long ExportByteLimit => (ExportMaxMegabytes is >= 1 and <= 200 ? ExportMaxMegabytes : 50) * 1024L * 1024L;

    /// <summary>The portal address to show: the configured one when it is <c>https</c> without credentials, otherwise the default.</summary>
    public string PortalUrl =>
        Uri.TryCreate(AlloggiatiPortalUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : DefaultAlloggiatiPortalUrl;
}
