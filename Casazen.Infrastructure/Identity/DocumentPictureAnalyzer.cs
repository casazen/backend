using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Opens one picture of an identity document for the checks of the self-hosted document step and for the zoned prefill of <c>read-document</c> (E71/F01/US03/T01, NF-189, NF-190):
/// the one place that decodes the upload, asks the geometry inspector about it, cuts the canonical card out of it and has its fields read by zone. The picture is held in memory only
/// and zeroed when the <see cref="DocumentPicture"/> is disposed. Strictly local: no AI provider, no HTTP client, no job, no logger; what it touches of a document is returned to the
/// caller, who decides what is kept (flags and hashes, never a value).
/// </summary>
public sealed class DocumentPictureAnalyzer(DocumentGeometryInspector geometry, IZonedFieldReader zonedFields, IOptions<BiometricModelOptions> options)
{
    /// <summary>
    /// Decodes <paramref name="encoded"/> (a JPEG or a PNG, the EXIF orientation applied) and holds the pixels for the checks. The bytes are the caller's, who zeroes them: nothing of
    /// them is kept. <see cref="InvalidDataException"/> when they are not a picture that can be decoded (not a JPEG or a PNG, damaged, over <c>Biometrics:MaxImagePixels</c>).
    /// </summary>
    public DocumentPicture Open(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        return new DocumentPicture(RasterImage.Decode(encoded, options.Value.MaxImagePixels), geometry, zonedFields);
    }

    /// <summary>
    /// The format the checks use while the document is not known yet, before an MRZ is read: the only registered card of two sides with a TD1 MRZ, the Italian electronic identity card
    /// (decision D1). A second ID-1 card of two sides, of another country, cannot be told from the first by its picture: it needs the MRZ read first and the format detected from it
    /// (<see cref="DocumentFormatRegistry.DetectFromMrz"/>), which the flow does as soon as a picture shows the MRZ; until then the first of them, by id, is used. Null when the registry
    /// has no such card (every document then goes to a person).
    /// </summary>
    public static DocumentFormat? DefaultCardFormat(IReadOnlyList<DocumentFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);

        return formats.FirstOrDefault(format =>
            format.Sides.SequenceEqual(DocumentFormatSides.TwoSided, StringComparer.Ordinal)
            && string.Equals(format.Mrz.Type, DocumentFormatMrzTypes.Td1, StringComparison.Ordinal));
    }

    /// <summary>The side of <paramref name="format"/> that has the portrait of the holder (the front of a card, the page of a passport); null when none has.</summary>
    public static DocumentSide? PortraitSideOf(DocumentFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        foreach (var (side, layout) in format.Layout.Sides)
        {
            if (layout.Portrait is not null && DocumentSides.TryParse(side, out var parsed))
                return parsed;
        }

        return null;
    }
}

/// <summary>
/// One decoded picture of an identity document (E71/F01/US03/T01): its difference hash, the geometry of its card, and the fields read on the card cut out of it and set upright.
/// A card that was not held straight (IDENT-TILT) is read on the card levelled (<see cref="DocumentGeometryReport.RectifiedCard"/>), not on the box cut out of the picture: the zones of the
/// format are fractions of a card that lies straight. The picture owns the levelled card of every report it made, so that nothing outlives it: zeroed by <see cref="Dispose"/>, which every
/// caller reaches in a <c>finally</c> (ADR-013-R2).
/// </summary>
public sealed class DocumentPicture : IDisposable
{
    private readonly RasterImage _image;
    private readonly DocumentGeometryInspector _geometry;
    private readonly IZonedFieldReader _zonedFields;
    private readonly List<DocumentGeometryReport> _reports = [];

    internal DocumentPicture(RasterImage image, DocumentGeometryInspector geometry, IZonedFieldReader zonedFields)
    {
        _image = image;
        _geometry = geometry;
        _zonedFields = zonedFields;
    }

    /// <summary>The difference hash of the picture (<see cref="PerceptualHash.DHash64"/>), the one the check of a pair of sides compares.</summary>
    public ulong DHash()
    {
        return PerceptualHash.DHash64(_image);
    }

    /// <summary>
    /// The geometry of the picture as <paramref name="declaredSide"/> of <paramref name="format"/> (<see cref="DocumentGeometryInspector.Inspect"/>). A model that is not available
    /// is <see cref="BiometricUnavailableException"/> and any other fault of the detector reaches the caller too: the caller sends the verification to a person.
    /// </summary>
    public DocumentGeometryReport Inspect(DocumentFormat format, DocumentSide declaredSide, CancellationToken cancellationToken)
    {
        return Keep(_geometry.Inspect(_image, format, declaredSide, cancellationToken));
    }

    /// <summary>
    /// <see cref="Inspect(DocumentFormat, DocumentSide, CancellationToken)"/> that may leave the face alone: with <paramref name="lookForPortrait"/> false the detector is never asked
    /// (<see cref="DocumentGeometryInspector.Inspect(RasterImage, DocumentFormat, DocumentSide, bool, CancellationToken)"/>), and the card is judged and turned by its box and its MRZ band.
    /// </summary>
    public DocumentGeometryReport Inspect(DocumentFormat format, DocumentSide declaredSide, bool lookForPortrait, CancellationToken cancellationToken)
    {
        return Keep(_geometry.Inspect(_image, format, declaredSide, lookForPortrait, cancellationToken));
    }

    /// <summary>The report of the picture, which the picture now owns (its levelled card, if it has one, is zeroed with the picture).</summary>
    private DocumentGeometryReport Keep(DocumentGeometryReport report)
    {
        _reports.Add(report);
        return report;
    }

    /// <summary>
    /// Whether the card of <paramref name="report"/> can be cut out and set upright: the whole card was found, its edges are visible, it is of the layout of its format and its
    /// orientation is decided. Only then are its zones where the layout of the format puts them.
    /// </summary>
    public static bool IsMeasurable(DocumentGeometryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.CardFound
            && report.CardBox is not null
            && report.EdgesVisible
            && report.AspectOk
            && !report.Codes.Contains(IdentityGuidanceCodes.DocumentOrientation, StringComparer.Ordinal);
    }

    /// <summary>
    /// The fields read by zone on <paramref name="side"/> of the card of <paramref name="report"/> (the report of <see cref="Inspect(DocumentFormat, DocumentSide, CancellationToken)"/> for this picture): the canonical
    /// card (cut out and turned upright, or, for a card that was not held straight, the card levelled that the report carries) is handed to the reader of the zones, which also reads the MRZ
    /// band on it and never throws. Empty when the card is not measurable. The values are personal data that live in the memory of the request only.
    /// </summary>
    public async Task<IReadOnlyList<ZonedField>> ReadFieldsAsync(
        DocumentFormat format, DocumentSide side, DocumentGeometryReport report, CancellationToken cancellationToken)
    {
        // The levelled card belongs to the report (and, through it, to the picture): the reader only looks at it, and it is not zeroed here.
        if (UsesRectifiedCard(report))
            return await _zonedFields.ReadAsync(report.RectifiedCard!, format, side, cancellationToken);

        using var card = CutCard(report);
        if (card is null)
            return [];

        return await _zonedFields.ReadAsync(card, format, side, cancellationToken);
    }

    /// <summary>
    /// Whether the card of <paramref name="report"/> is read on the card levelled the report carries (IDENT-TILT): the card is measurable and was turned by more than
    /// <see cref="DocumentGeometryRules.TiltRoundingStepDegrees"/> (the inspector made the levelled copy for it). A card that lies straight is cut as it lies, with no extra copy.
    /// </summary>
    internal static bool UsesRectifiedCard(DocumentGeometryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return IsMeasurable(report)
            && report.RectifiedCard is { Rgb.Length: > 0 }
            && Math.Abs(report.TiltDegrees) > DocumentGeometryRules.TiltRoundingStepDegrees;
    }

    /// <summary>
    /// The canonical card cut out as it lies: the box of the card of <paramref name="report"/> cut out of the upright picture and turned by its rotation, so the card lies upright and landscape; null
    /// when the card is not measurable. A copy that belongs to the caller, who zeroes it. A card that was not held straight is not cut here (<see cref="UsesRectifiedCard"/>): its box is larger
    /// than it and its zones would be crooked.
    /// </summary>
    internal RasterImage? CutCard(DocumentGeometryReport report)
    {
        if (!IsMeasurable(report) || report.CardBox is not { } box)
            return null;

        var left = (int)Math.Floor(box.X * _image.Width);
        var top = (int)Math.Floor(box.Y * _image.Height);
        var right = (int)Math.Ceiling((box.X + box.W) * _image.Width);
        var bottom = (int)Math.Ceiling((box.Y + box.H) * _image.Height);
        var cut = _image.Cropped(left, top, right - left, bottom - top);
        if (report.ExifOrientation == 1)
            return cut;

        try
        {
            return cut.Oriented(report.ExifOrientation);
        }
        finally
        {
            cut.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var report in _reports)
            report.Dispose();

        _reports.Clear();
        _image.Dispose();
    }
}
