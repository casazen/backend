using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The scan of the files of the owner portal (E07/F07/US01/T02): CasaZen has no antivirus engine in its stack, so the files are judged by their content,
/// strictly. Only three types pass, recognised from their bytes (never from the name or the declared type): PDF, JPEG and PNG. A PDF passes only without
/// active content: no script (<c>/JavaScript</c>, <c>/JS</c>), no launch or remote action (<c>/Launch</c>, <c>/GoToR</c>, <c>/GoToE</c>), no embedded file,
/// no rich media, no XFA form and no form that submits or imports data; the names are read also inside the streams written with the <c>FlateDecode</c>
/// filter (zlib: the object streams of PDF 1.5), with their <c>#xx</c> escapes decoded, and a file whose streams would inflate beyond 50 MB is refused as
/// unreadable. <b>What this scan cannot read it refuses</b>: a PDF that is encrypted (a reader opens it without a password) and one that writes a stream with
/// another filter that turns it into text (<c>ASCIIHexDecode</c>, <c>ASCII85Decode</c>, <c>LZWDecode</c>, <c>RunLengthDecode</c>, <c>Crypt</c>, also by their
/// short names) are refused with the code of active content, since an object stream written that way would hide the names above; the filters of the images
/// (<c>DCTDecode</c>, <c>CCITTFaxDecode</c>, <c>JBIG2Decode</c>, <c>JPXDecode</c>) hold no names and pass. This is a strict content check, not an antivirus
/// engine: it also refuses, rarely, a legitimate PDF whose binary data happens to hold one of these names. An antivirus engine is another implementation
/// of <see cref="IOwnerFileScanner"/>; every stored file names the engine that judged it.
/// </summary>
public sealed partial class OwnerFileContentScanner : IOwnerFileScanner
{
    /// <summary>The name of this engine, as the files record it.</summary>
    public const string EngineName = "casazen-content-rules-1";

    /// <summary>The most bytes all the streams of one PDF may inflate to.</summary>
    private const long MaxInflatedBytes = 50L * 1024 * 1024;

    /// <summary>The most streams of one PDF that are read.</summary>
    private const int MaxStreams = 5000;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The names a PDF must not hold: active content (scripts, launch and remote actions, embedded files, rich media, XFA, forms that submit or import) and what
    /// keeps the scan from reading the rest of the file: <c>Encrypt</c> (and <c>Crypt</c>), and the filters that write a stream as text this scan does not decode,
    /// with the short names the readers accept for them in any stream.
    /// </summary>
    private static readonly HashSet<string> RefusedNames = new(StringComparer.Ordinal)
    {
        "JavaScript", "JS", "Launch", "GoToR", "GoToE", "EmbeddedFile", "EmbeddedFiles", "RichMedia", "XFA", "SubmitForm", "ImportData",
        "Encrypt", "Crypt", "ASCIIHexDecode", "AHx", "ASCII85Decode", "A85", "LZWDecode", "LZW", "RunLengthDecode", "RL",
    };

    public string Engine => EngineName;

    public OwnerFileVerdict Scan(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            return OwnerFileVerdict.Rejected(OwnerFileRules.Missing);
        if (content.Length > OwnerFileRules.MaxBytes)
            return OwnerFileVerdict.Rejected(OwnerFileRules.TooLarge);

        if (content.AsSpan().StartsWith("%PDF-"u8))
            return ScanPdf(content);
        if (content.Length > 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
            return OwnerFileVerdict.Accepted(OwnerFileRules.Jpeg, ".jpg");
        if (content.AsSpan().StartsWith(PngSignature))
            return OwnerFileVerdict.Accepted(OwnerFileRules.Png, ".png");

        return OwnerFileVerdict.Rejected(OwnerFileRules.TypeNotAllowed);
    }

    private static OwnerFileVerdict ScanPdf(byte[] content)
    {
        // Latin-1 maps every byte to one character: the indexes of the text are the offsets of the file.
        var text = Encoding.Latin1.GetString(content);
        if (HasActiveName(text))
            return OwnerFileVerdict.Rejected(OwnerFileRules.ActiveContent);

        // Every place a stream may start, in one pass over the text. The data of a stream is inflated from there: the zlib data ends where it ends (the rest of the
        // file is not read), so no search for «endstream» is needed (it was one search per stream, a cost of streams times bytes) and a stream cannot be cut short
        // by an «endstream» written inside its data.
        long inflated = 0;
        var streams = 0;
        foreach (Match start in StreamStart().Matches(text))
        {
            if (++streams > MaxStreams)
                return OwnerFileVerdict.Rejected(OwnerFileRules.ActiveContent);

            var dataStart = start.Index + start.Length;
            switch (TryInflate(content, dataStart, content.Length - dataStart, MaxInflatedBytes - inflated, out var data))
            {
                case InflateOutcome.NotZlib:
                    continue;
                case InflateOutcome.TooLarge:
                    return OwnerFileVerdict.Rejected(OwnerFileRules.ActiveContent);
            }

            inflated += data.Length;
            if (HasActiveName(Encoding.Latin1.GetString(data)))
                return OwnerFileVerdict.Rejected(OwnerFileRules.ActiveContent);
        }

        return OwnerFileVerdict.Accepted(OwnerFileRules.Pdf, ".pdf");
    }

    private enum InflateOutcome
    {
        /// <summary>Not zlib data: an image or a stream of another filter, which holds no PDF name to read.</summary>
        NotZlib,

        /// <summary>Inflated within the budget.</summary>
        Done,

        /// <summary>Inflates beyond what is left of the budget: the file cannot be read safely.</summary>
        TooLarge,
    }

    /// <summary>Inflates the zlib data of a stream into <paramref name="data"/>, at most <paramref name="budget"/> bytes.</summary>
    private static InflateOutcome TryInflate(byte[] content, int offset, int length, long budget, out byte[] data)
    {
        data = [];
        if (length <= 2)
            return InflateOutcome.NotZlib;

        try
        {
            using var source = new MemoryStream(content, offset, length, writable: false);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                if (target.Length > budget)
                    return InflateOutcome.TooLarge;
            }

            data = target.ToArray();
            return InflateOutcome.Done;
        }
        catch (InvalidDataException)
        {
            return InflateOutcome.NotZlib;
        }
    }

    /// <summary>True when the text holds a PDF name of <see cref="RefusedNames"/> (active content, or what this scan cannot read), its <c>#xx</c> escapes decoded.</summary>
    private static bool HasActiveName(string text)
    {
        foreach (Match name in PdfName().Matches(text))
        {
            var value = name.Groups[1].Value;
            if (value.Contains('#', StringComparison.Ordinal))
                value = HexEscape().Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

            if (RefusedNames.Contains(value))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?<!end)stream\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex StreamStart();

    [GeneratedRegex(@"/([^\s/<>\[\]\(\)\{\}%]{1,127})", RegexOptions.CultureInvariant)]
    private static partial Regex PdfName();

    [GeneratedRegex("#([0-9A-Fa-f]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex HexEscape();
}
