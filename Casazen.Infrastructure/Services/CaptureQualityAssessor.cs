using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The rules of the capture checks (E03/F04/US02/T02), versioned: a change of a value is a new <see cref="Version"/>, kept with every outcome in
/// the log. Provisional values, set on pictures built in the tests; calibrate them with real captures before the flag is turned on.
/// </summary>
public static class CaptureQualityRules
{
    /// <summary>The version of these rules, written in <c>BiometricVerificationLog.ThresholdsJson</c>.</summary>
    public const string Version = "capture-2026.11-1";

    /// <summary>Under this mean luminance (0 to 255) the face, or the document, is too dark.</summary>
    public const double MinMeanLuminance = 55;

    /// <summary>Over this mean luminance the face, or the document, is too bright.</summary>
    public const double MaxMeanLuminance = 210;

    /// <summary>Over this share of the edge energy that survives a 3 x 3 blur the picture is blurred (crisp text or skin is well under it).</summary>
    public const double MaxBlurRatio = 0.92;

    /// <summary>Over this share of saturated pixels (250 or more) a reflection hides part of the document.</summary>
    public const double MaxDocumentGlareShare = 0.08;

    /// <summary>The longest side the picture is brought to before the pixel statistics (enough for them, and fast).</summary>
    public const int StatisticsSide = 640;
}

/// <summary>
/// The in-memory checks of one capture (E03/F04/US02/T02, AC-NF51-1, AC-NF51-2). Selfie: no face, several faces, face too small (under
/// <c>Biometrics:MinFaceAreaRatio</c>), face too dark or too bright, face blurred. Document: too dark or too bright, glare, blur, no face photo.
/// The MRZ of a document is checked by its reading (<see cref="CaptureGuidanceCodes.FromMrzQuality"/>), not here. Glasses and masks cannot be
/// detected by the models: a selfie always carries them as static tips. Nothing is kept: the gray copies are zeroed before returning, and the
/// picture is the caller's.
/// </summary>
public sealed class CaptureQualityAssessor(IFaceDetector faceDetector, IOptions<BiometricModelOptions> options)
{
    private static readonly IReadOnlyList<string> SelfieTips = [CaptureGuidanceCodes.TipRemoveGlasses, CaptureGuidanceCodes.TipRemoveMask];

    /// <summary>
    /// The assessment of <paramref name="image"/>. <see cref="CaptureAssessment.Assessed"/> is false when the face models are not available
    /// (<see cref="BiometricUnavailableException"/>): the pixel checks of a document still give their guidance.
    /// </summary>
    public CaptureAssessment Assess(RasterImage image, CaptureKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        return kind == CaptureKind.Selfie ? AssessSelfie(image, cancellationToken) : AssessDocument(image, cancellationToken);
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

        var guidance = new List<string>();
        if (faces.Count == 0)
        {
            guidance.Add(CaptureGuidanceCodes.ShowYourFace);

            // With no face the whole picture tells whether the light was the problem.
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

    private CaptureAssessment AssessDocument(RasterImage image, CancellationToken cancellationToken)
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
        try
        {
            if (faceDetector.Detect(image, options.Value.DocumentDetectionScoreThreshold, cancellationToken).Count == 0)
                guidance.Add(CaptureGuidanceCodes.ShowDocumentPhoto);
        }
        catch (BiometricUnavailableException)
        {
            assessed = false;
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

    /// <summary>
    /// A gray copy of the face box (or of the whole picture) brought within <see cref="CaptureQualityRules.StatisticsSide"/>; null when the box
    /// is empty. The caller disposes it (zeroed).
    /// </summary>
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

    /// <summary>The mean of the gray values, 0 to 255.</summary>
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
