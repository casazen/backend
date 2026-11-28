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
    public const string Version = "geometry-2026.11-2";

    public const int EdgeFloor = 40;

    public const double EdgeRelative = 0.2;

    public const double PercentileShare = 0.99;

    public const int DominanceFactor = 2;

    public const int BandRadius = 2;

    /// <summary>
    /// The share of the length of a side that must be edge pixels, in one row (or column) within <see cref="BandRadius"/> of the side, for the side to be a line of the picture:
    /// it decides whether a side of the card is seen, so whether the card is whole (<c>document_edges_not_visible</c>) and of its layout. One row, not the union of the rows near
    /// it: pieces of edge at different heights (a line of text and the top of the portrait) are not a straight edge together, and neither is an edge that is turned.
    /// </summary>
    public const double SideSupportMin = 0.75;

    public const int MaxLineCandidates = 8;

    public const int MinWorkingSide = 32;

    public const int SmallestCardSideDivisor = 8;

    public const double MaxTiltDegrees = 10.0;


    public const double TiltSearchDegrees = 20.0;

    public const double TiltCoarseStepDegrees = 0.5;

    public const double TiltFineStepDegrees = 0.05;

    public const double TiltRoundingStepDegrees = 0.25;

    public const int TiltCandidates = 3;

    public const double TiltPeakFactor = 2.0;

    public const double TiltPeakSeparationDegrees = 2.0;

    public const double TiltPlateauShare = 0.95;
}
