namespace Casazen.Core.Options;

/// <summary>
/// One model file of the identity verification (E03/F04/US01, ADR-013-R8 and R14): what it is, where it comes from and the SHA-256 it must have.
/// A file is used only when its hash is configured and equal to the one of the bytes on disk; a missing file, a missing hash or a different one
/// makes the capability unavailable (the check-in then goes to the operator), never a silent success.
/// </summary>
public class ModelFileOptions
{
    /// <summary>Name of the model as it is logged and stored with a verification (for example <c>YuNet</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Version of the model (release or file date), logged and stored with a verification.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Licence of the weights (ADR-013-R14).</summary>
    public string License { get; set; } = string.Empty;

    /// <summary>Note on the provenance of the training data (ADR-013-R14, the open risk of the DPIA).</summary>
    public string Provenance { get; set; } = string.Empty;

    /// <summary>Expected SHA-256 of the file, 64 hexadecimal characters. Empty means "not pinned yet": the file is not used.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Location of the file: relative to the application folder (the small models shipped with the application) or absolute.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// <c>https</c> address the file is downloaded from when it is not on disk (the large models, ADR-013-R8). The download is kept only when
    /// its SHA-256 equals <see cref="Sha256"/>; without a pinned hash nothing is downloaded.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>True when <see cref="Sha256"/> is a well-formed SHA-256 (64 hexadecimal characters).</summary>
    public bool HasWellFormedSha256()
    {
        var hash = Sha256.Trim();
        return hash.Length == 64 && hash.All(Uri.IsHexDigit);
    }

    /// <summary>Configuration errors of this entry, empty when it is usable. Names the setting, never a value.</summary>
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
