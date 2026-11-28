using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public static class CaptureQualityRules
{
    public const string Version = "capture-2026.11-1";

    public const double MinMeanLuminance = 55;

    public const double MaxMeanLuminance = 210;

    public const double MaxBlurRatio = 0.92;

    public const double MaxDocumentGlareShare = 0.08;

    public const int StatisticsSide = 640;
}

/// <summary>
/// The in-memory checks of one capture (E03/F04/US02/T02, AC-NF51-1, AC-NF51-2). Selfie: no face, several faces, face too small (under
/// <c>Biometrics:MinFaceAreaRatio</c>), face too dark or too bright, face blurred. Document: too dark or too bright, glare, blur, no face photo
/// (the back of a card, <see cref="CaptureKind.DocumentBack"/>, is not asked for a face: the photo is on the front, E22/F01/US01/T02).
/// The MRZ of a document is checked by its reading (<see cref="CaptureGuidanceCodes.FromMrzQuality"/>), not here. Glasses and masks cannot be
/// detected by the models: a selfie always carries them as static tips. Nothing is kept: the gray copies are zeroed before returning, and the
/// picture is the caller's.
/// </summary>
public sealed class CaptureQualityAssessor(IFaceDetector faceDetector, IOptions<BiometricModelOptions> options)
{
    private static readonly IReadOnlyList<string> SelfieTips = [CaptureGuidanceCodes.TipRemoveGlasses, CaptureGuidanceCodes.TipRemoveMask];

    public CaptureAssessment Assess(RasterImage image, CaptureKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        return kind == CaptureKind.Selfie
            ? AssessSelfie(image, cancellationToken)
            : AssessDocument(image, kind != CaptureKind.DocumentBack, cancellationToken);
    }

    private CaptureAssessment AssessSelfie(RasterImage image, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        IReadOnlyList<DetectedFace> faces;
        try
        {
            faces = faceDetector.Detect(image, settings.DetectionScoreThreshold, cancellationToken);
        }
        catch (BiometricUnavailableException)
        {
            return CaptureAssessment.NotAssessed with { Tips = SelfieTips };
        }

        return AssessSelfie(image, faces, settings);
    }

    internal static CaptureAssessment AssessSelfie(RasterImage image, IReadOnlyList<DetectedFace> faces, BiometricModelOptions settings)
    {
        var guidance = new List<string>();
        if (faces.Count == 0)
        {
            guidance.Add(CaptureGuidanceCodes.ShowYourFace);

            using var whole = Statistics(image, null);
            AddLight(guidance, whole);
            return new CaptureAssessment(false, guidance, SelfieTips, true);
        }

        if (faces.Count > 1)
            guidance.Add(CaptureGuidanceCodes.OneFaceOnly);

        var face = faces[0];
        if (face.AreaRatio < settings.MinFaceAreaRatio)
            guidance.Add(CaptureGuidanceCodes.MoveCloser);

        using (var region = Statistics(image, face))
        {
            AddLight(guidance, region);
            AddBlur(guidance, region);
        }

        return new CaptureAssessment(guidance.Count == 0, guidance, SelfieTips, true);
    }

    private CaptureAssessment AssessDocument(RasterImage image, bool expectFace, CancellationToken cancellationToken)
    {
        var guidance = new List<string>();
        using (var gray = Statistics(image, null))
        {
            AddLight(guidance, gray);
            if (gray is not null && MrzImageLocator.SaturatedShare(gray) > CaptureQualityRules.MaxDocumentGlareShare)
                guidance.Add(CaptureGuidanceCodes.AvoidGlare);
            AddBlur(guidance, gray);
        }

        var assessed = true;
        if (expectFace)
        {
            try
            {
                var portrait = DocumentPortrait.Find(faceDetector, image, options.Value, cancellationToken, out _, requireAlignment: false);
                if (portrait is null)
                    guidance.Add(CaptureGuidanceCodes.ShowDocumentPhoto);
            }
            catch (BiometricUnavailableException)
            {
                assessed = false;
            }
        }

        return new CaptureAssessment(assessed && guidance.Count == 0, guidance, [], assessed);
    }

    private static void AddLight(List<string> guidance, GrayBitmap? gray)
    {
        if (gray is null)
            return;

        var mean = MeanLuminance(gray);
        if (mean < CaptureQualityRules.MinMeanLuminance)
            guidance.Add(CaptureGuidanceCodes.MoreLight);
        else if (mean > CaptureQualityRules.MaxMeanLuminance)
            guidance.Add(CaptureGuidanceCodes.LessLight);
    }

    private static void AddBlur(List<string> guidance, GrayBitmap? gray)
    {
        if (gray is not null && MrzImageLocator.BlurRatio(gray) > CaptureQualityRules.MaxBlurRatio)
            guidance.Add(CaptureGuidanceCodes.HoldStill);
    }

    internal static GrayBitmap? Statistics(RasterImage image, DetectedFace? face)
    {
        using var whole = image.ToGray();
        var region = face is null
            ? new GrayBitmap(whole.Width, whole.Height, (byte[])whole.Pixels.Clone())
            : whole.Cropped((int)face.X, (int)face.Y, (int)Math.Ceiling(face.Width), (int)Math.Ceiling(face.Height));
        if (region is null)
            return null;

        var longest = Math.Max(region.Width, region.Height);
        if (longest <= CaptureQualityRules.StatisticsSide)
            return region;

        using (region)
        {
            var scale = (double)CaptureQualityRules.StatisticsSide / longest;
            return region.Resized(Math.Max(1, (int)(region.Width * scale)), Math.Max(1, (int)(region.Height * scale)));
        }
    }

    internal static double MeanLuminance(GrayBitmap gray)
    {
        if (gray.Pixels.Length == 0)
            return 0;

        long sum = 0;
        foreach (var pixel in gray.Pixels)
            sum += pixel;

        return (double)sum / gray.Pixels.Length;
    }
}
