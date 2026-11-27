using Casazen.Core.Identity;

namespace Casazen.Infrastructure.Identity;

// E71/F01/US01/T01 (NF-189, ADR-013-R17..R22): what the geometry of a picture of an identity document says. Numbers, flags and guidance codes only: never a pixel,
// a value read on the document or a face (the report may be logged by its codes and its numbers, and stored in DocumentCheckJson as flags).

/// <summary>
/// A rectangle of a picture: every member is a share of the width (<paramref name="X"/>, <paramref name="W"/>) or of the height (<paramref name="Y"/>,
/// <paramref name="H"/>) of that picture, from 0 to 1, measured from its top left corner. The box of the card is in the picture the inspector was given (the
/// upright picture, with the EXIF orientation applied), before any rotation.
/// </summary>
/// <param name="X">The distance of the left edge from the left border of the picture.</param>
/// <param name="Y">The distance of the top edge from the top border of the picture.</param>
/// <param name="W">The width.</param>
/// <param name="H">The height.</param>
public sealed record NormalizedBox(double X, double Y, double W, double H);

/// <summary>
/// What <see cref="DocumentGeometryInspector"/> found in one picture of an identity document: whether the whole card is there, with its edges visible, of the
/// layout of its format, upright, with the portrait where the layout puts it, without glare or blur. The inspector decides nothing else: the guidance of the
/// flow (E71/F01/US03/T01) turns <paramref name="Codes"/> into the reason the guest sees.
/// </summary>
/// <param name="CardFound">A rectangle bounded by lines of the picture was found. False: nothing like a card is in the picture (<c>document_not_found</c>).</param>
/// <param name="CardBox">
/// The card as a box of the picture; null when no card was found. A card that is cut by the frame has the border of the picture as the side that runs out of it:
/// its box is the visible part. A card that is not turned keeps the box it has; the rotation is <paramref name="RotationDegrees"/>.
/// </param>
/// <param name="EdgesVisible">
/// The four edges of the card are lines of the picture, the card keeps at least <c>Layout.MarginMin</c> from every border of the picture and covers at least
/// <c>Layout.MinCardAreaShare</c> of it. False: a side is cut by the frame, too close to the border, or the card is too small (<c>document_edges_not_visible</c>).
/// </param>
/// <param name="AspectRatio">The long side over the short side of <paramref name="CardBox"/>, in the pixels of the picture; 0 when no card was found. It is the ratio of a card only when <paramref name="EdgesVisible"/>.</param>
/// <param name="AspectOk">
/// All four edges were found and the long over short ratio is within <c>Layout.AspectTolerance</c> of <c>Layout.AspectRatio</c>. False as well when the ratio could not be
/// measured (a side is missing): the layout is judged (<c>document_layout_mismatch</c>) only when <paramref name="EdgesVisible"/>.
/// </param>
/// <param name="RotationDegrees">
/// 0, 90, 180 or 270: how many degrees <b>clockwise</b> the picture must be turned to make the card the canonical upright landscape card (<see cref="ExifOrientation"/> is
/// the same turn as <c>RasterImage.Oriented</c> takes it). 90 and 270 are tall cards; 180 is a card upside down. Decided on the card box, then, on a side that has the portrait, on
/// the turn at which the face is found and, on a side that has an MRZ band, on the band. 0 when the card is not measurable (it is not asked to turn before it is whole).
/// </param>
/// <param name="PortraitInZone">
/// A face lies in the portrait zone of the format. Null when it was not asked: the format has no portrait zone, or the card is not measurable whole yet, or the face could only
/// be found sideways on an upright card. Evaluated on any side of a format that has a portrait zone, so that the side classifier can tell a front sent as the back
/// (<c>document_portrait_missing</c> is raised only when the declared side is the one that has the portrait).
/// </param>
/// <param name="GlareShare">The share of saturated pixels on the card (the whole picture when no card was found), as <c>CaptureQualityAssessor</c> measures it: over <c>CaptureQualityRules.MaxDocumentGlareShare</c> it is <c>document_glare</c>.</param>
/// <param name="BlurRatio">The share of the edge energy that survives a blur, on the card (the whole picture when no card was found): over <c>CaptureQualityRules.MaxBlurRatio</c> it is <c>document_blur</c>.</param>
/// <param name="Codes">
/// Codes of <see cref="IdentityGuidanceCodes"/>, in this order: <c>document_not_found</c>, <c>document_edges_not_visible</c>, <c>document_layout_mismatch</c>,
/// <c>document_orientation</c> (only when the rotation cannot be decided), <c>document_portrait_missing</c>, <c>document_glare</c>, <c>document_blur</c>. Empty: nothing to ask the guest.
/// </param>
public sealed record DocumentGeometryReport(
    bool CardFound,
    NormalizedBox? CardBox,
    bool EdgesVisible,
    double AspectRatio,
    bool AspectOk,
    int RotationDegrees,
    bool? PortraitInZone,
    double GlareShare,
    double BlurRatio,
    IReadOnlyList<string> Codes)
{
    /// <summary>The value of <c>RasterImage.Oriented</c> that turns the picture by <see cref="RotationDegrees"/>: 1 upright, 6 a quarter turn clockwise, 3 a half turn, 8 a quarter turn counter-clockwise.</summary>
    public int ExifOrientation => RotationDegrees switch
    {
        90 => 6,
        180 => 3,
        270 => 8,
        _ => 1,
    };
}
