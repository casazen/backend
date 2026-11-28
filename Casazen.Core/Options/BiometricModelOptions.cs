namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Biometrics</c>: the models of the face check of the guest check-in (E03/F04/US01, ADR-013, NF-24, NF-51) and the limits of one
/// verification. On Railway the variables are <c>Biometrics__FaceRecognition__Sha256</c>, <c>Biometrics__FaceRecognition__DownloadUrl</c>, and so on.
/// <para>
/// Nothing here is a secret. The three models are loaded once, in the background, at startup of an environment where
/// <c>Features:CheckInIdentityVerification</c> is on; a model whose file is missing, whose hash is not pinned or does not match is not used and
/// the verification endpoints answer with the operator path (<c>Escalate</c>), never with a "live" or a "match". Operator steps, sources and
/// licences: <c>docs/TECHNICAL.md</c> (Identity verification models) and <c>Casazen.Web/MlModels/README.md</c>.
/// </para>
/// </summary>
public class BiometricModelOptions
{
    public const string SectionName = "Biometrics";

    public const int DefaultVerificationTimeoutMs = 2500;

    public ModelFileOptions FaceDetector { get; set; } = new()
    {
        Name = "YuNet",
        Version = "2023mar",
        License = "MIT",
        Provenance = "Trained on WIDER FACE by its authors; not independently verified (ADR-013 risk, DPIA gate).",
        Path = "MlModels/biometrics/face_detection_yunet_2023mar.onnx",
    };

    public ModelFileOptions AntiSpoofing { get; set; } = new()
    {
        Name = "MiniFASNetV2",
        Version = "2.7_80x80",
        License = "Apache-2.0",
        Provenance = "Training data collected by the authors, not published; to be confirmed in the DPIA (ADR-013 gate).",
        Path = "MlModels/biometrics/2.7_80x80_MiniFASNetV2.onnx",
    };

    public ModelFileOptions FaceRecognition { get; set; } = new()
    {
        Name = "SFace",
        Version = "2021dec",
        License = "Apache-2.0",
        Provenance = "OpenCV Zoo face_recognition_sface, 2021dec release.",
        Path = "MlModels/biometrics/face_recognition_sface_2021dec.onnx",
    };

    /// <summary>
    /// The optional second candidate for the anti-spoofing model (E72/F01/US02/T03, decision D6): a plug-in point only. Empty (the committed state) is <b>not configured</b>: nothing is
    /// loaded and the role reports <c>not_configured</c>. Configured, it is verified by its SHA-256 like the three models above and used only by the offline comparison of the
    /// qualification, <b>never by a decision</b>: the readiness of the models, the health entry and every verification ignore it. No weight file of a candidate is in the repository.
    /// </summary>
    public AntiSpoofingCandidateOptions AntiSpoofingCandidate { get; set; } = new();

    public FaceRecognitionCandidateOptions FaceRecognitionCandidate { get; set; } = new();

    public int DetectorInputSize { get; set; } = 640;

    public double DetectionScoreThreshold { get; set; } = 0.8;

    public double DocumentDetectionScoreThreshold { get; set; } = 0.6;

    public double NmsIouThreshold { get; set; } = 0.3;

    public double MinFaceAreaRatio { get; set; } = 0.08;

    public double LivenessThreshold { get; set; } = 0.7;

    public double LivenessCropScale { get; set; } = 2.7;

    public int LivenessInputSize { get; set; } = 80;

    public int RecognitionInputSize { get; set; } = 112;

    public double RecognitionMean { get; set; }

    public double RecognitionStd { get; set; } = 1;

    public bool RecognitionRgbOrder { get; set; } = true;

    public int VerificationTimeoutMs { get; set; } = DefaultVerificationTimeoutMs;

    public int LivenessTimeoutMs { get; set; } = 1500;

    public int DownloadTimeoutMinutes { get; set; } = 30;

    public int IntraOpThreads { get; set; } = 2;

    public int MaxImageBytes { get; set; } = 12_000_000;

    public int MaxImagePixels { get; set; } = 25_000_000;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        failures.AddRange(FaceDetector.Validate($"{SectionName}:FaceDetector"));
        failures.AddRange(AntiSpoofing.Validate($"{SectionName}:AntiSpoofing"));
        failures.AddRange(FaceRecognition.Validate($"{SectionName}:FaceRecognition"));
        if (DetectorInputSize is < 160 or > 1920)
            failures.Add($"{SectionName}:DetectorInputSize must be between 160 and 1920.");
        CheckFraction(failures, nameof(DetectionScoreThreshold), DetectionScoreThreshold);
        CheckFraction(failures, nameof(DocumentDetectionScoreThreshold), DocumentDetectionScoreThreshold);
        CheckFraction(failures, nameof(NmsIouThreshold), NmsIouThreshold);
        CheckFraction(failures, nameof(MinFaceAreaRatio), MinFaceAreaRatio);
        CheckFraction(failures, nameof(LivenessThreshold), LivenessThreshold);
        if (LivenessCropScale is < 1 or > 8)
            failures.Add($"{SectionName}:LivenessCropScale must be between 1 and 8.");
        if (LivenessInputSize is < 16 or > 512)
            failures.Add($"{SectionName}:LivenessInputSize must be between 16 and 512.");
        if (RecognitionInputSize is < 32 or > 512)
            failures.Add($"{SectionName}:RecognitionInputSize must be between 32 and 512.");
        if (RecognitionStd <= 0)
            failures.Add($"{SectionName}:RecognitionStd must be positive.");
        if (VerificationTimeoutMs is < 500 or > 30_000)
            failures.Add($"{SectionName}:VerificationTimeoutMs must be between 500 and 30000.");
        if (LivenessTimeoutMs is < 200 or > 30_000)
            failures.Add($"{SectionName}:LivenessTimeoutMs must be between 200 and 30000.");
        if (DownloadTimeoutMinutes is < 1 or > 240)
            failures.Add($"{SectionName}:DownloadTimeoutMinutes must be between 1 and 240.");
        if (IntraOpThreads is < 0 or > 64)
            failures.Add($"{SectionName}:IntraOpThreads must be between 0 and 64.");
        if (MaxImageBytes is < 1000 or > 100_000_000)
            failures.Add($"{SectionName}:MaxImageBytes must be between 1000 and 100000000.");
        if (MaxImagePixels is < 10_000 or > 100_000_000)
            failures.Add($"{SectionName}:MaxImagePixels must be between 10000 and 100000000.");
        return failures;
    }

    public static bool IsCandidateConfigured(ModelFileOptions candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return !string.IsNullOrWhiteSpace(candidate.Path) || !string.IsNullOrWhiteSpace(candidate.Sha256) || !string.IsNullOrWhiteSpace(candidate.DownloadUrl);
    }

    public IReadOnlyList<string> ValidateAntiSpoofingCandidate()
    {
        if (!IsCandidateConfigured(AntiSpoofingCandidate))
            return [];

        var setting = $"{SectionName}:{nameof(AntiSpoofingCandidate)}";
        var failures = new List<string>(AntiSpoofingCandidate.Validate(setting));
        if (double.IsNaN(AntiSpoofingCandidate.CropScale) || AntiSpoofingCandidate.CropScale is < 1 or > 8)
            failures.Add($"{setting}:CropScale must be between 1 and 8.");
        if (AntiSpoofingCandidate.InputSize is < 16 or > 512)
            failures.Add($"{setting}:InputSize must be between 16 and 512.");
        return failures;
    }

    public IReadOnlyList<string> ValidateFaceRecognitionCandidate()
    {
        if (!IsCandidateConfigured(FaceRecognitionCandidate))
            return [];

        var setting = $"{SectionName}:{nameof(FaceRecognitionCandidate)}";
        var failures = new List<string>(FaceRecognitionCandidate.Validate(setting));
        if (FaceRecognitionCandidate.InputSize is < 32 or > 512)
            failures.Add($"{setting}:InputSize must be between 32 and 512.");
        if (!double.IsFinite(FaceRecognitionCandidate.Mean))
            failures.Add($"{setting}:Mean must be a number.");
        if (!double.IsFinite(FaceRecognitionCandidate.Std) || FaceRecognitionCandidate.Std <= 0)
            failures.Add($"{setting}:Std must be positive.");
        return failures;
    }

    public BiometricModelOptions ForAntiSpoofingCandidate()
    {
        var copy = (BiometricModelOptions)MemberwiseClone();
        copy.LivenessCropScale = AntiSpoofingCandidate.CropScale;
        copy.LivenessInputSize = AntiSpoofingCandidate.InputSize;
        return copy;
    }

    public BiometricModelOptions ForFaceRecognitionCandidate()
    {
        var copy = (BiometricModelOptions)MemberwiseClone();
        copy.RecognitionInputSize = FaceRecognitionCandidate.InputSize;
        copy.RecognitionMean = FaceRecognitionCandidate.Mean;
        copy.RecognitionStd = FaceRecognitionCandidate.Std;
        copy.RecognitionRgbOrder = FaceRecognitionCandidate.RgbOrder;
        return copy;
    }

    private static void CheckFraction(List<string> failures, string name, double value)
    {
        if (double.IsNaN(value) || value <= 0 || value >= 1)
            failures.Add($"{SectionName}:{name} must be between 0 and 1 (exclusive).");
    }
}

public class AntiSpoofingCandidateOptions : ModelFileOptions
{
    public double CropScale { get; set; } = 2.7;

    public int InputSize { get; set; } = 80;
}

public class FaceRecognitionCandidateOptions : ModelFileOptions
{
    public int InputSize { get; set; } = 112;

    public double Mean { get; set; }

    public double Std { get; set; } = 1;

    public bool RgbOrder { get; set; } = true;
}
