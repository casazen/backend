namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The rules of the geometry checks of the document (E71/F01/US01, NF-189), versioned like <c>CaptureQualityRules</c>: the parameters of the edge detector of
/// <see cref="DocumentGeometryInspector"/> that decide whether a card is found, whether its sides are lines of the picture and how small a card can still be looked for, and the
/// limits of the search of the angle of a card that is not held straight (IDENT-TILT). The layout of a format (ratio, tolerance, margin, area, zones) is in the format file; how an
/// edge is seen and how much turn a card may have is not a property of a format, so it lives here. Every value is provisional until the qualification on real photos (D12): a change of
/// any of them is a new <see cref="Version"/>, and <see cref="DocumentGeometryReport.RulesVersion"/> carries it with every report so that the flow can keep it with the outcome.
/// <c>DocumentGeometryRulesTests</c> holds each value next to its version: a value changed under the same version fails it.
/// </summary>
public static class DocumentGeometryRules
{
    /// <summary>
    /// The version of these rules, carried by every <see cref="DocumentGeometryReport"/> (<see cref="DocumentGeometryReport.RulesVersion"/>). <c>geometry-2026.11-1</c> read the card as it
    /// lies (a turn of a fifth of a degree either way was its whole tolerance); <c>geometry-2026.11-2</c> adds the search of the angle (<see cref="MaxTiltDegrees"/> and the values below it).
    /// </summary>
    public const string Version = "geometry-2026.11-2";

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

    /// <summary>
    /// The most the card may be turned in the plane of the picture, in degrees either way, and still be read (a card held by hand is turned by one to eight degrees almost every time): the
    /// card is levelled by its angle, up to this, and its zones are read on the levelled card. A card that is found turned by more is refused, <c>document_edges_not_visible</c> (the guest is
    /// asked to hold it straighter, not told that there is no card), and nothing of it is read.
    /// </summary>
    public const double MaxTiltDegrees = 10.0;


    /// <summary>
    /// How far, in degrees either way, the angle of a card is looked for: the limit of what a card may be turned by and still be recognised as a card. Between <see cref="MaxTiltDegrees"/>
    /// and this a card is found and refused for its turn; beyond it nothing is looked for (<c>document_not_found</c>). Within the reach of the classes of the edges of the detector (an edge is
    /// horizontal or vertical up to a turn whose tangent is the inverse of <see cref="DominanceFactor"/>, 26 degrees).
    /// </summary>
    public const double TiltSearchDegrees = 20.0;

    /// <summary>
    /// The coarse step of the search of the angle, in degrees: the edges of the picture are scored at every angle of the search this far from the next (the sides of a card, a few hundred pixels
    /// long, are straight to within a fraction of a degree at their best angle, so this step cannot step over it).
    /// </summary>
    public const double TiltCoarseStepDegrees = 0.5;

    /// <summary>The fine step of the search of the angle, in degrees: around each peak of the coarse search the angles are scored by this step, and it is the precision the angle of a card is known to.</summary>
    public const double TiltFineStepDegrees = 0.05;

    /// <summary>
    /// The rounding step of <see cref="DocumentGeometryReport.TiltDegrees"/>, in degrees: a card turned by no more than this is read as it lies (no levelled copy of it is made,
    /// <see cref="DocumentGeometryReport.RectifiedCard"/> is null; its zones are a pixel or two out at the far end of the card, which the margin of the reader of the zones covers), and an angle
    /// of the search closer to none than half of it is the picture as it lies, which was read already.
    /// </summary>
    public const double TiltRoundingStepDegrees = 0.25;

    /// <summary>How many angles, the likeliest first, are tried for a card whose picture is not supported as it lies: each costs a levelled copy and a search of the card in it.</summary>
    public const int TiltCandidates = 3;

    /// <summary>
    /// How much a peak of the search of the angle must stand out for its angle to be tried, as a multiple of the median score of the angles of the search: below it the edges of the picture have no
    /// angle they line up at (no long straight object), so there is no angle to look for and the answer stays the one of the picture as it lies.
    /// </summary>
    public const double TiltPeakFactor = 2.0;

    /// <summary>The least distance, in degrees, between two angles that are tried: the peaks nearer than this are one peak (<see cref="TiltCandidates"/> angles are tried, not the same one three times).</summary>
    public const double TiltPeakSeparationDegrees = 2.0;

    /// <summary>
    /// The share of the best score of the fine scan an angle must reach to be as good as the best: the angle of a card is the middle (the score-weighted mean) of those, not the first of a flat top.
    /// </summary>
    public const double TiltPlateauShare = 0.95;
}
