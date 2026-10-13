namespace Casazen.Web.Infrastructure;

/// <summary>Stable <c>code</c> values of the file storage errors (FD-07), see <see cref="ApiProblemDetails"/>.</summary>
public static class StorageProblemCodes
{
    /// <summary>The document row exists but its file is not in the storage (lost with the old container disk).</summary>
    public const string DocumentFileMissing = "document_file_missing";

    /// <summary>The configured storage provider cannot sign URLs (filesystem provider): use the authenticated download.</summary>
    public const string SignedUrlUnavailable = "signed_url_unavailable";
}
