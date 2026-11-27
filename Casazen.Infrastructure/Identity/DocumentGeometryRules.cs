namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The rules of the geometry checks of the document (E71/F01/US01, NF-189), versioned like <c>CaptureQualityRules</c>: the parameters of the edge detector of
/// <see cref="DocumentGeometryInspector"/> that decide whether a card is found, whether its sides are lines of the picture and how small a card can still be looked for. The
/// layout of a format (ratio, tolerance, margin, area, zones) is in the format file; how an edge is seen is not a property of a format, so it lives here. Every value is
/// provisional until the qualification on real photos (D12): a change of any of them is a new <see cref="Version"/>, and <see cref="DocumentGeometryReport.RulesVersion"/>
/// carries it with every report so that the flow can keep it with the outcome. <c>DocumentGeometryRulesTests</c> holds each value next to its version: a value
/// changed under the same version fails it.
/// </summary>
public static class DocumentGeometryRules
{
    /// <summary>The version of these rules, carried by every <see cref="DocumentGeometryReport"/> (<see cref="DocumentGeometryReport.RulesVersion"/>).</summary>
    public const string Version = "geometry-2026.11-1";

    /// <summary>The gradient a pixel must have, whatever the picture, to be an edge (the Sobel gradient of a step of 10 gray levels is 40).</summary>
    public const int EdgeFloor = 40;

    /// <summary>The share of the strongest gradients of the picture (its 99th percentile) a pixel must reach to be an edge: a crisp picture raises the bar above the floor.</summary>
    public const double EdgeRelative = 0.2;

    /// <summary>The share of the pixels whose gradient the percentile above is taken on.</summary>
    public const double PercentileShare = 0.99;

    /// <summary>A pixel is on a horizontal edge when its vertical gradient is at least this many times its horizontal one (and the other way round for a vertical edge).</summary>
    public const int DominanceFactor = 2;

    /// <summary>How far, in pixels of the gray copy, the row or the column that supports a side may be from the peak of the profile (the edge of a card is a pixel or two wide).</summary>
    public const int BandRadius = 2;

    /// <summary>
    /// The share of the length of a side that must be edge pixels, in one row (or column) within <see cref="BandRadius"/> of the side, for the side to be a line of the picture:
    /// it decides whether a side of the card is seen, so whether the card is whole (<c>document_edges_not_visible</c>) and of its layout. One row, not the union of the rows near
    /// it: pieces of edge at different heights (a line of text and the top of the portrait) are not a straight edge together, and neither is an edge that is turned.
    /// </summary>
    public const double SideSupportMin = 0.75;

    /// <summary>The most rows (and as many columns) kept as candidates for a side of the card, the strongest first.</summary>
    public const int MaxLineCandidates = 8;

    /// <summary>A picture with a side shorter than this has no card to find.</summary>
    public const int MinWorkingSide = 32;

    /// <summary>
    /// A side of a card shorter than this part of the shorter side of the picture (an eighth) is not looked for: the zones of the format would be a few pixels wide. It decides
    /// whether a small card is not found (<c>document_not_found</c>) or found but too small (<c>document_edges_not_visible</c>).
    /// </summary>
    public const int SmallestCardSideDivisor = 8;
}
