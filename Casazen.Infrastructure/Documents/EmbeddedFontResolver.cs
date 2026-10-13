using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace Casazen.Infrastructure.Documents;

/// <summary>
/// Fonts of every generated PDF (LT-09): DejaVu Sans 2.37 (Bitstream Vera license, see
/// <c>Documents/Fonts/DejaVu-LICENSE.txt</c>), compiled into this assembly and embedded (subset) in the PDF. Every
/// family resolves to it, so rendering never reads the fonts of the host: the Railway container has none.
/// DejaVu Sans covers Latin with all its extensions, Greek and Cyrillic; CJK scripts are not covered.
/// </summary>
internal sealed class EmbeddedFontResolver : IFontResolver
{
    public const string FamilyName = "DejaVu Sans";
    internal const string RegularFace = "DejaVuSans";
    internal const string BoldFace = "DejaVuSans-Bold";

    private static readonly Lock RegistrationGate = new();

    private readonly ConcurrentDictionary<string, byte[]> _faces = new(StringComparer.Ordinal);

    private EmbeddedFontResolver()
    {
    }

    public static EmbeddedFontResolver Instance { get; } = new();

    /// <summary>
    /// Makes <see cref="Instance"/> the font resolver of PDFsharp. PDFsharp keeps one resolver per process and accepts
    /// it only before the first font is used, so every renderer calls this before rendering.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (ReferenceEquals(GlobalFontSettings.FontResolver, Instance))
            return;

        lock (RegistrationGate)
        {
            if (ReferenceEquals(GlobalFontSettings.FontResolver, Instance))
                return;
            if (GlobalFontSettings.FontResolver is not null)
                throw new InvalidOperationException(
                    $"Another PDFsharp font resolver ({GlobalFontSettings.FontResolver.GetType().Name}) is registered: PDFs must use only the embedded fonts.");

            GlobalFontSettings.FontResolver = Instance;
        }
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? BoldFace : RegularFace, mustSimulateBold: false, mustSimulateItalic: isItalic);

    public byte[] GetFont(string faceName) => _faces.GetOrAdd(faceName, LoadFace);

    private static byte[] LoadFace(string faceName)
    {
        var resource = faceName switch
        {
            RegularFace or BoldFace => $"Casazen.Infrastructure.Documents.Fonts.{faceName}.ttf",
            _ => throw new ArgumentException($"Unknown embedded font face '{faceName}'.", nameof(faceName)),
        };

        using var stream = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded font resource '{resource}' not found in {typeof(EmbeddedFontResolver).Assembly.GetName().Name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
