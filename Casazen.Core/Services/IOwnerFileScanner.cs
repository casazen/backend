using System.Globalization;
using System.Text;

namespace Casazen.Core.Services;

/// <summary>
/// Judges a file before the owner portal stores it (E07/F07/US01/T02, AC-NF111-3: the malware scan of the shared documents and of the attachments of the
/// inbox). A file that is not judged clean is never stored and never reaches an owner. The engine is named on every stored file, so an engine changed later
/// says which files it has not judged.
/// </summary>
public interface IOwnerFileScanner
{
    string Engine { get; }

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

public static class OwnerFileRules
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";

    public const string TooLarge = "owner_file_too_large";

    public const string Missing = "owner_file_missing";

    public const string TypeNotAllowed = "owner_file_type_not_allowed";

    public const string ActiveContent = "owner_file_active_content";

    public const string TooLargeMessageKey = "OwnerFileTooLarge";
    public const string MissingMessageKey = "OwnerFileMissing";
    public const string TypeNotAllowedMessageKey = "OwnerFileTypeNotAllowed";
    public const string ActiveContentMessageKey = "OwnerFileActiveContent";

    public static string MessageKeyOf(string code) => code switch
    {
        TooLarge => TooLargeMessageKey,
        Missing => MissingMessageKey,
        ActiveContent => ActiveContentMessageKey,
        _ => TypeNotAllowedMessageKey,
    };

    public static string ExtensionOf(string? contentType) => contentType switch
    {
        Pdf => ".pdf",
        Jpeg => ".jpg",
        Png => ".png",
        _ => string.Empty,
    };

    /// <summary>
    /// The name a download offers: the name the upload came with, made safe, and <b>the extension the scan recognised</b> (<paramref name="extension"/>), never the
    /// one that came with it. Only the last part of a path is kept (whatever its separator), the extension sent is dropped and every other dot with it (so
    /// «fattura.pdf.exe» and «a.pdf.bat» become «fattura_pdf.pdf» and «a_pdf.pdf»: no double extension, no script or program name), the control characters and
    /// the invisible and direction characters of Unicode (the right-to-left override that makes «gpj.exe» read «exe.jpg») are dropped, and anything that is not
    /// a letter, a digit, a space or one of <c>- _ ( ) ,</c> becomes <c>_</c>. Cut to <paramref name="maxLength"/> with the extension; <c>file</c> when nothing is left.
    /// </summary>
    public static string SafeFileName(string? name, string extension, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var bare = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').Split('/').Last()).Trim();
        var stem = new StringBuilder();
        foreach (var c in Path.GetFileNameWithoutExtension(bare))
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;

            stem.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')' or ',' ? c : '_');
        }

        var clean = stem.ToString().Trim(' ', '_');
        if (clean.Length == 0)
            clean = "file";
        var room = Math.Max(1, maxLength - extension.Length);
        return (clean.Length <= room ? clean : clean[..room].TrimEnd(' ', '_')) + extension;
    }
}
