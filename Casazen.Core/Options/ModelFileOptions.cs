namespace Casazen.Core.Options;

/// <summary>
/// One model file of the identity verification (E03/F04/US01, ADR-013-R8 and R14): what it is, where it comes from and the SHA-256 it must have.
/// A file is used only when its hash is configured and equal to the one of the bytes on disk; a missing file, a missing hash or a different one
/// makes the capability unavailable (the check-in then goes to the operator), never a silent success.
/// </summary>
public class ModelFileOptions
{
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string License { get; set; } = string.Empty;

    public string Provenance { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public bool HasWellFormedSha256()
    {
        var hash = Sha256.Trim();
        return hash.Length == 64 && hash.All(Uri.IsHexDigit);
    }

    public IReadOnlyList<string> Validate(string setting)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            failures.Add($"{setting}:Name is required.");
        if (string.IsNullOrWhiteSpace(Path))
            failures.Add($"{setting}:Path is required.");
        else if (!System.IO.Path.IsPathRooted(Path) && Path.Replace('\\', '/').Split('/').Contains(".."))
            failures.Add($"{setting}:Path must not leave the application folder.");
        if (!string.IsNullOrWhiteSpace(Sha256) && !HasWellFormedSha256())
            failures.Add($"{setting}:Sha256 must be 64 hexadecimal characters.");
        if (!string.IsNullOrWhiteSpace(DownloadUrl)
            && !(Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps))
            failures.Add($"{setting}:DownloadUrl must be an absolute https address.");
        return failures;
    }
}
