using Casazen.Core.Identity;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// What the check of a pair of pictures of a document needs to know about one picture (E71/F01/US01/T02): the SHA-256 of its bytes (hexadecimal text in either case, the way
/// the flow keeps it; empty when the flow has none), its difference hash (<see cref="PerceptualHash.DHash64"/>) and the side it shows as <see cref="DocumentSideClassifier.Classify"/>
/// told it (null when it could not be told). These are derived values of the document: the flow keeps them in <c>DocumentCheckJson</c> only while the verification runs and clears
/// them at the closure (E73), and they are never logged, so <see cref="ToString"/> prints none of them.
/// </summary>
/// <param name="Sha256">The SHA-256 of the bytes of the picture, in hexadecimal; empty when the flow has none.</param>
/// <param name="DHash">
/// The difference hash of the picture; null when the flow does not have it (for example <see cref="PerceptualHash.TryParseHex"/> refused the text it kept). A missing hash is not 0:
/// 0 is the hash of a plain picture. While the hash of either picture is missing the hashes are not compared, and only the SHA-256 and the side can say the pictures are the same.
/// </param>
/// <param name="Side">The side the picture shows, by its content; null when it could not be told.</param>
public sealed record DocumentPictureFingerprint(string Sha256, ulong? DHash, DocumentSide? Side)
{
    public override string ToString()
    {
        return nameof(DocumentPictureFingerprint);
    }
}

public sealed class DocumentSideClassifier
{
    /// <summary>
    /// The side a picture shows, from what was found in it: <paramref name="geometry"/> says whether the portrait is in its zone (<see cref="DocumentGeometryReport.PortraitInZone"/>,
    /// evaluated whatever side was declared) and <paramref name="mrzFound"/> whether an MRZ was found. The side is told by the format: a two-sided format has the MRZ on one side
    /// (<see cref="DocumentFormatMrz.Side"/>) and the portrait on a side of its layout; a picture with the MRZ shows the side that has it (for the Italian card the back, even when a
    /// face is found too, since the front has the portrait and no MRZ), else one with the portrait in its zone shows the side that has the portrait (the front), else it is
    /// <c>null</c> (unknown: the flow asks for the document again, <c>document_not_found</c> or <c>document_layout_mismatch</c>). A format of one side has nothing to tell apart: its
    /// picture is <see cref="DocumentSide.Single"/> when it shows the portrait or the MRZ, else null.
    /// </summary>
    public DocumentSide? Classify(DocumentGeometryReport geometry, bool mrzFound, DocumentFormat format)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(format);

        var portrait = geometry.PortraitInZone == true;
        if (format.Sides.Count == 1)
            return mrzFound || portrait ? DocumentSide.Single : null;

        if (mrzFound)
            return SideOf(format.Mrz.Side);

        return portrait ? SideOf(PortraitSide(format)) : null;
    }

    /// <summary>
    /// Whether <paramref name="picture"/>, sent as <paramref name="declaredSide"/>, can be the picture of that side next to <paramref name="other"/>, the picture the flow holds
    /// for the other side (null while the other side has not been sent). The code of <see cref="IdentityGuidanceCodes"/> to ask the guest for a new capture, or null when the pair is
    /// fine. <c>document_side_duplicate</c> when the SHA-256 is the other side's (the same file), when both pictures have a difference hash and their distance is at most
    /// <paramref name="perceptualDuplicateMaxDistance"/> (the same picture of the same side, photographed again; a missing hash never makes a duplicate), or when both pictures
    /// show the same side (two fronts); it
    /// comes first, since the guest is asked for the other side either way. <c>document_wrong_side</c> when the picture shows a side and it is not the declared one: the guest is
    /// asked for the other side, not punished. A picture of an unknown side is neither (the geometry asked for it already), and nothing is wrong with a side declared as
    /// <see cref="DocumentSide.Single"/>, the side of a format that has one.
    /// </summary>
    public string? CheckPair(DocumentSide declaredSide, DocumentPictureFingerprint picture, DocumentPictureFingerprint? other, int perceptualDuplicateMaxDistance)
    {
        ArgumentNullException.ThrowIfNull(picture);

        if (other is not null && IsDuplicate(picture, other, perceptualDuplicateMaxDistance))
            return IdentityGuidanceCodes.DocumentSideDuplicate;

        if (declaredSide != DocumentSide.Single && picture.Side is { } shown && shown != declaredSide)
            return IdentityGuidanceCodes.DocumentWrongSide;

        return null;
    }

    private static bool IsDuplicate(DocumentPictureFingerprint picture, DocumentPictureFingerprint other, int maxDistance)
    {
        if (!string.IsNullOrEmpty(picture.Sha256) && string.Equals(picture.Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase))
            return true;

        if (picture.DHash is { } hash && other.DHash is { } otherHash && PerceptualHash.Distance(hash, otherHash) <= maxDistance)
            return true;

        return picture.Side is { } side && other.Side == side;
    }

    private static DocumentSide? SideOf(string? code)
    {
        return code switch
        {
            DocumentSides.Front => DocumentSide.Front,
            DocumentSides.Back => DocumentSide.Back,
            _ => null,
        };
    }

    private static string? PortraitSide(DocumentFormat format)
    {
        foreach (var (side, layout) in format.Layout.Sides)
        {
            if (layout.Portrait is not null)
                return side;
        }

        return null;
    }
}
