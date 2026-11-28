using Casazen.Core.Identity;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

// E71/F01/US01/T01 (NF-189, ADR-013-R17..R22): what the geometry of a picture of an identity document says. Numbers, flags and guidance codes only: never a pixel,
// a value read on the document or a face (the report may be logged by its codes and its numbers, and stored in DocumentCheckJson as flags). The one exception is the
// levelled card of a card that was not held straight (IDENT-TILT): a copy in memory for the readers of the document step, which the owner of the report zeroes and which is
// never logged, stored or sent.

public sealed record NormalizedBox(double X, double Y, double W, double H);

/// <summary>
/// What <see cref="DocumentGeometryInspector"/> found in one picture of an identity document: whether the whole card is there, with its edges visible, of the
/// layout of its format, upright, with the portrait where the layout puts it, without glare or blur. The inspector decides nothing else: the guidance of the
/// flow (E71/F01/US03/T01) turns <paramref name="Codes"/> into the reason the guest sees. A report that carries a <paramref name="RectifiedCard"/> owns a picture held in memory:
/// whoever owns the report disposes it (<see cref="Dispose"/>), which zeroes the picture; <c>DocumentPicture</c> does it for the reports of its picture.
/// </summary>
/// <param name="CardFound">A rectangle bounded by lines of the picture was found. False: nothing like a card is in the picture (<c>document_not_found</c>).</param>
/// <param name="CardBox">
/// The card as a box of the picture; null when no card was found. A card that is cut by the frame has the border of the picture as the side that runs out of it:
/// its box is the visible part. A card that is not turned keeps the box it has; the rotation is <paramref name="RotationDegrees"/>. The box of a card that is not held straight
/// (<paramref name="TiltDegrees"/> is not 0) is the box of its four corners in the picture, so it is larger than the card; <paramref name="AspectRatio"/> is the one of the card.
/// </param>
/// <param name="EdgesVisible">
/// The four edges of the card are lines of the picture, the card keeps at least <c>Layout.MarginMin</c> from every border of the picture, covers at least
/// <c>Layout.MinCardAreaShare</c> of it and is turned by no more than <see cref="DocumentGeometryRules.MaxTiltDegrees"/>. False: a side is cut by the frame, too close to the border, the
/// card is too small, or it is held more crooked than that (<c>document_edges_not_visible</c>).
/// </param>
/// <param name="AspectRatio">The long side over the short side of the card, in the pixels of the picture (the box for a card that is not held straight is not the card); 0 when no card was found. It is the ratio of a card only when <paramref name="EdgesVisible"/>.</param>
/// <param name="AspectOk">
/// All four edges were found and the long over short ratio is within <c>Layout.AspectTolerance</c> of <c>Layout.AspectRatio</c>. False as well when the ratio could not be
/// measured (a side is missing): the layout is judged (<c>document_layout_mismatch</c>) only when <paramref name="EdgesVisible"/>.
/// </param>
/// <param name="RotationDegrees">
/// 0, 90, 180 or 270: how many degrees <b>clockwise</b> the picture must be turned to make the card the canonical upright landscape card (<see cref="ExifOrientation"/> is
/// the same turn as <c>RasterImage.Oriented</c> takes it). 90 and 270 are tall cards; 180 is a card upside down. Decided on the card box, then, on a side that has an MRZ band and
/// no portrait (the back), on the band, before any face (a face found on it never turns it); on a side that has the portrait, on the turn at which the face is found, and with no
/// face on the band of the side. 0 when the card is not measurable (it is not asked to turn before it is whole). The turn of a card that is not held straight is the one it has after it is levelled.
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
/// <param name="RulesVersion">
/// The version of the rules of the edge detector this report was made with (<see cref="DocumentGeometryRules.Version"/>). The flow keeps it with the outcome, so that a
/// change of one of the values of <see cref="DocumentGeometryRules"/> can be told from a change of the pictures.
/// </param>
/// <param name="TiltDegrees">
/// IDENT-TILT: how many degrees <b>clockwise</b> the card is turned in the plane of the picture, measured after the quarter turn of <paramref name="RotationDegrees"/> (so a card upside down and
/// held a little crooked has the same tilt as the same card upright): positive when its top edge goes down to the right, negative when it goes up. Known to
/// <see cref="DocumentGeometryRules.TiltRoundingStepDegrees"/>; 0 for a card that lies straight (a card that is straight to within a fifth of a degree is read as it lies and is 0 as well), and for
/// a picture with no card. A card turned by more than <see cref="DocumentGeometryRules.MaxTiltDegrees"/> has its angle here and <paramref name="EdgesVisible"/> false.
/// </param>
/// <param name="RectifiedCard">
/// IDENT-TILT: the card levelled by <paramref name="TiltDegrees"/> and turned by <paramref name="RotationDegrees"/>, at the resolution of the picture, cut to the card: what the card looks like
/// when it is held straight, upright and landscape, so that the zones of the format (fractions of the card) are where the layout says. In memory only; null when the card lies straight (or is
/// turned by no more than <see cref="DocumentGeometryRules.TiltRoundingStepDegrees"/>) and when the card is not measurable (it is whole, visible, of the ratio and its turn is decided). The document
/// step hands it, and not the picture, to the reader of the zones, the MRZ reading and the portrait detection when it is there. The owner of the report zeroes it, <see cref="Dispose"/>.
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
    IReadOnlyList<string> Codes,
    string RulesVersion,
    double TiltDegrees = 0,
    RasterImage? RectifiedCard = null) : IDisposable
{
    public int ExifOrientation => ExifOrientationOf(RotationDegrees);

    public static int ExifOrientationOf(int rotationDegrees) => rotationDegrees switch
    {
        90 => 6,
        180 => 3,
        270 => 8,
        _ => 1,
    };

    public void Dispose()
    {
        RectifiedCard?.Dispose();
    }
}
