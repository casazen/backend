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
    public DocumentPicture Open(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        return new DocumentPicture(RasterImage.Decode(encoded, options.Value.MaxImagePixels), geometry, zonedFields);
    }

    public static DocumentFormat? DefaultCardFormat(IReadOnlyList<DocumentFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);

        return formats.FirstOrDefault(format =>
            format.Sides.SequenceEqual(DocumentFormatSides.TwoSided, StringComparer.Ordinal)
            && string.Equals(format.Mrz.Type, DocumentFormatMrzTypes.Td1, StringComparison.Ordinal));
    }

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

    public ulong DHash()
    {
        return PerceptualHash.DHash64(_image);
    }

    public DocumentGeometryReport Inspect(DocumentFormat format, DocumentSide declaredSide, CancellationToken cancellationToken)
    {
        return Keep(_geometry.Inspect(_image, format, declaredSide, cancellationToken));
    }

    public DocumentGeometryReport Inspect(DocumentFormat format, DocumentSide declaredSide, bool lookForPortrait, CancellationToken cancellationToken)
    {
        return Keep(_geometry.Inspect(_image, format, declaredSide, lookForPortrait, cancellationToken));
    }

    private DocumentGeometryReport Keep(DocumentGeometryReport report)
    {
        _reports.Add(report);
        return report;
    }

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
        return await ReadOnCardAsync(report, card => _zonedFields.ReadAsync(card, format, side, cancellationToken)) ?? [];
    }

    internal async Task<T?> ReadOnCardAsync<T>(DocumentGeometryReport report, Func<RasterImage, Task<T>> read)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(read);

        // The levelled card belongs to the report; a card cut out is a copy zeroed when the reading ends, so the reader must not keep it.
        if (UsesRectifiedCard(report))
            return await read(report.RectifiedCard!);

        using var card = CutCard(report);
        return card is null ? null : await read(card);
    }

    internal static bool UsesRectifiedCard(DocumentGeometryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return IsMeasurable(report)
            && report.RectifiedCard is { Rgb.Length: > 0 }
            && Math.Abs(report.TiltDegrees) > DocumentGeometryRules.TiltRoundingStepDegrees;
    }

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
