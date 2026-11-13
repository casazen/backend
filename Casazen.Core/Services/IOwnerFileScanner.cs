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

    /// <summary>The extension, with its dot, a file of the type the scan recognised takes in the name of a download; empty for any other type.</summary>
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
