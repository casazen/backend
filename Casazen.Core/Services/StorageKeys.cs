using System.Text.RegularExpressions;

namespace Casazen.Core.Services;

/// <summary>
/// Builds and validates object keys of <see cref="IFileStorage"/>. One place for the layout:
/// <list type="bullet">
/// <item><c>properties/{propertyId}/photos/{file}</c> — public bucket</item>
/// <item><c>suppliers/{supplierOrgId}/photos/{file}</c> — public bucket</item>
/// <item><c>properties/{propertyId}/documents/{file}</c> — private bucket</item>
/// <item><c>guest-documents/{orgId}/{guestId}/{file}</c> — private bucket</item>
/// </list>
/// Private keys are what the database stores for private files (<c>PropertyDocument.StorageUrl</c>,
/// <c>Guest.DocumentScanUrl</c>); public files are stored as their absolute public URL.
/// </summary>
public static partial class StorageKeys
{
    public static string PropertyPhoto(Guid propertyId, string fileName) =>
        $"properties/{propertyId}/photos/{fileName}";

    public static string SupplierPhoto(Guid supplierOrgId, string fileName) =>
        $"suppliers/{supplierOrgId}/photos/{fileName}";

    public static string PropertyDocument(Guid propertyId, string fileName) =>
        $"properties/{propertyId}/documents/{fileName}";

    public static string GuestDocument(Guid orgId, Guid guestId, string fileName) =>
        $"guest-documents/{orgId}/{guestId}/{fileName}";

    /// <summary>RLI registration receipt of a lease (private bucket, LT-01).</summary>
    public static string LeaseRegistrationReceipt(Guid orgId, Guid leaseId, string fileName) =>
        $"leases/{orgId}/{leaseId}/registration/{fileName}";

    /// <summary>A new random file name that keeps only the (lower-cased) extension of the upload.</summary>
    public static string NewFileName(string? originalFileName) =>
        $"{Guid.NewGuid()}{Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant()}";

    /// <summary>
    /// True for a relative key made only of safe segments: no leading slash, no <c>.</c>/<c>..</c>
    /// segments, no backslashes, only letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.
    /// </summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || !SafeKey().IsMatch(key))
            return false;

        return key.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="key"/> is not <see cref="IsValid"/>.</summary>
    public static string EnsureValid(string? key) =>
        IsValid(key) ? key! : throw new ArgumentException("Invalid storage key.", nameof(key));

    /// <summary>MIME type for the extension of a stored file (download responses).</summary>
    public static string ContentTypeFor(string? fileName) =>
        Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    [GeneratedRegex("^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeKey();
}
