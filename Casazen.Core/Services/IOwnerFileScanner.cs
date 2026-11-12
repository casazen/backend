namespace Casazen.Core.Services;

/// <summary>
/// Judges a file before the owner portal stores it (E07/F07/US01/T02, AC-NF111-3: the malware scan of the shared documents and of the attachments of the
/// inbox). A file that is not judged clean is never stored and never reaches an owner. The engine is named on every stored file, so an engine changed later
/// says which files it has not judged.
/// </summary>
public interface IOwnerFileScanner
{
    /// <summary>The name and version of the engine, as the files record it.</summary>
    string Engine { get; }

    /// <summary>The verdict on <paramref name="content"/>, the whole file (at most <see cref="OwnerFileRules.MaxBytes"/>).</summary>
    OwnerFileVerdict Scan(byte[] content);
}

/// <summary>What the scan found: the type recognised from the content when the file is clean, the stable code of the reason otherwise.</summary>
/// <param name="Clean">True when the file may be stored.</param>
/// <param name="ContentType">The type recognised from the bytes (never the one declared by the upload).</param>
/// <param name="Extension">The extension of that type (<c>.pdf</c>, <c>.jpg</c>, <c>.png</c>).</param>
/// <param name="RejectionCode">One of <see cref="OwnerFileRules"/> codes when not clean.</param>
public sealed record OwnerFileVerdict(bool Clean, string? ContentType, string? Extension, string? RejectionCode)
{
    public static OwnerFileVerdict Accepted(string contentType, string extension) => new(true, contentType, extension, null);

    public static OwnerFileVerdict Rejected(string code) => new(false, null, null, code);
}

/// <summary>The rules of the files of the owner portal, with the stable codes of a refusal.</summary>
public static class OwnerFileRules
{
    /// <summary>The largest file: 10 MB.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";

    /// <summary>413: the file is larger than <see cref="MaxBytes"/>.</summary>
    public const string TooLarge = "owner_file_too_large";

    /// <summary>400: no file, or an empty one.</summary>
    public const string Missing = "owner_file_missing";

    /// <summary>422: not a PDF, a JPEG or a PNG by its content (whatever its name or declared type say).</summary>
    public const string TypeNotAllowed = "owner_file_type_not_allowed";

    /// <summary>422: a PDF with active content (scripts, launch actions, embedded files, forms that submit) or that cannot be read safely.</summary>
    public const string ActiveContent = "owner_file_active_content";

    public const string TooLargeMessageKey = "OwnerFileTooLarge";
    public const string MissingMessageKey = "OwnerFileMissing";
    public const string TypeNotAllowedMessageKey = "OwnerFileTypeNotAllowed";
    public const string ActiveContentMessageKey = "OwnerFileActiveContent";

    /// <summary>The message key of a rejection code.</summary>
    public static string MessageKeyOf(string code) => code switch
    {
        TooLarge => TooLargeMessageKey,
        Missing => MissingMessageKey,
        ActiveContent => ActiveContentMessageKey,
        _ => TypeNotAllowedMessageKey,
    };

    /// <summary>The name of an uploaded file without any folder, cut to <paramref name="maxLength"/>; <c>file</c> when nothing is left.</summary>
    public static string SafeFileName(string? name, int maxLength)
    {
        var bare = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').Split('/').Last()).Trim();
        bare = new string(bare.Where(c => !char.IsControl(c)).ToArray());
        if (bare.Length == 0)
            bare = "file";
        return bare.Length <= maxLength ? bare : bare[^maxLength..];
    }
}
