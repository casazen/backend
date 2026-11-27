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

    /// <summary>ADR-013-R1: a verification answers within three seconds; the pipeline stops a little before.</summary>
    public const int DefaultVerificationTimeoutMs = 2500;

    /// <summary>Face detector and landmarks (YuNet, MIT, ~230 KB): shipped with the application.</summary>
    public ModelFileOptions FaceDetector { get; set; } = new()
    {
        Name = "YuNet",
        Version = "2023mar",
        License = "MIT",
        Provenance = "Trained on WIDER FACE by its authors; not independently verified (ADR-013 risk, DPIA gate).",
        Path = "MlModels/biometrics/face_detection_yunet_2023mar.onnx",
    };

    /// <summary>Anti-spoofing (MiniFASNetV2 of Silent-Face-Anti-Spoofing, Apache-2.0, ~2 MB): shipped with the application.</summary>
    public ModelFileOptions AntiSpoofing { get; set; } = new()
    {
        Name = "MiniFASNetV2",
        Version = "2.7_80x80",
        License = "Apache-2.0",
        Provenance = "Training data collected by the authors, not published; to be confirmed in the DPIA (ADR-013 gate).",
        Path = "MlModels/biometrics/2.7_80x80_MiniFASNetV2.onnx",
    };

    /// <summary>SFace 2021dec, distributed with the API under the OpenCV Zoo model license.</summary>
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

    /// <summary>The optional second candidate for the recognition model (E72/F01/US02/T03, decision D6): same rules as <see cref="AntiSpoofingCandidate"/>, plus the normalization the candidate was trained with.</summary>
    public FaceRecognitionCandidateOptions FaceRecognitionCandidate { get; set; } = new();

    /// <summary>Longest side of the picture given to the detector, in pixels (multiples of 32 are used). Default 640.</summary>
    public int DetectorInputSize { get; set; } = 640;

    /// <summary>Detections of the selfie below this score are ignored. Default 0.8 (provisional, to be calibrated).</summary>
    public double DetectionScoreThreshold { get; set; } = 0.8;

    /// <summary>Detections of the photo of the document below this score are ignored. Lower than the selfie: a printed photo scores less. Default 0.6.</summary>
    public double DocumentDetectionScoreThreshold { get; set; } = 0.6;

    /// <summary>Boxes overlapping more than this (intersection over union) are one face. Default 0.3.</summary>
    public double NmsIouThreshold { get; set; } = 0.3;

    /// <summary>Smallest face of the selfie, as the share of the picture its box covers (ADR-013, NF-51). A smaller one is rejected. Default 0.08.</summary>
    public double MinFaceAreaRatio { get; set; } = 0.08;

    /// <summary>Score of the real-face class under which the selfie is not live (provisional, to be calibrated on real data). Default 0.7.</summary>
    public double LivenessThreshold { get; set; } = 0.7;

    /// <summary>How much the face box is enlarged before it is cut for the anti-spoofing model (2.7 for MiniFASNetV2, 4.0 for MiniFASNetV1SE).</summary>
    public double LivenessCropScale { get; set; } = 2.7;

    /// <summary>Side of the square cut given to the anti-spoofing model. Default 80.</summary>
    public int LivenessInputSize { get; set; } = 80;

    /// <summary>Side of the aligned face given to the recognition model. Default 112.</summary>
    public int RecognitionInputSize { get; set; } = 112;

    /// <summary>Value taken from every pixel before the recognition model (127.5 for AuraFace and ArcFace models, 0 for SFace).</summary>
    public double RecognitionMean { get; set; }

    /// <summary>Divisor of every pixel after the mean (127.5 for AuraFace and ArcFace models, 1 for SFace).</summary>
    public double RecognitionStd { get; set; } = 1;

    /// <summary>True when the recognition model wants red, green, blue (all the models above); false for blue, green, red.</summary>
    public bool RecognitionRgbOrder { get; set; } = true;

    /// <summary>Time one verification may take, in milliseconds, liveness included (ADR-013-R1). Past it the answer is the operator path.</summary>
    public int VerificationTimeoutMs { get; set; } = DefaultVerificationTimeoutMs;

    /// <summary>Time the liveness check alone may take, in milliseconds.</summary>
    public int LivenessTimeoutMs { get; set; } = 1500;

    /// <summary>Minutes a model download may take before it is given up.</summary>
    public int DownloadTimeoutMinutes { get; set; } = 30;

    /// <summary>Threads per model run. The bounded default matches appsettings; an explicit 0 lets ONNX Runtime decide.</summary>
    public int IntraOpThreads { get; set; } = 2;

    /// <summary>Largest picture accepted, in bytes (the endpoint limit is 10 MB).</summary>
    public int MaxImageBytes { get; set; } = 12_000_000;

    /// <summary>Largest picture accepted, in pixels, checked before it is decoded (a small file can hold a huge picture).</summary>
    public int MaxImagePixels { get; set; } = 25_000_000;

    /// <summary>Configuration errors, empty when the section is usable. Names the setting, never a value.</summary>
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

    /// <summary>
    /// True when anything of an optional candidate is set: a path, a SHA-256 or a download address. A candidate with none of the three is <b>not configured</b>: nothing is loaded, nothing is
    /// validated and nothing is reported as wrong.
    /// </summary>
    public static bool IsCandidateConfigured(ModelFileOptions candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return !string.IsNullOrWhiteSpace(candidate.Path) || !string.IsNullOrWhiteSpace(candidate.Sha256) || !string.IsNullOrWhiteSpace(candidate.DownloadUrl);
    }

    /// <summary>
    /// Configuration errors of the anti-spoofing candidate, empty when it is not configured or usable. <b>Never part of <see cref="Validate"/></b>: a candidate that is wrong must not take the
    /// three models of the decisions down with it, so the store reads this apart and a failure leaves only the candidate unloaded (<c>config_invalid</c>). Names the setting, never a value.
    /// </summary>
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

    /// <summary>Configuration errors of the recognition candidate, empty when it is not configured or usable; apart from <see cref="Validate"/> like <see cref="ValidateAntiSpoofingCandidate"/>.</summary>
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

    /// <summary>
    /// A copy of these settings with the face cut for the anti-spoofing the way the candidate wants it (<see cref="AntiSpoofingCandidateOptions.CropScale"/> and
    /// <see cref="AntiSpoofingCandidateOptions.InputSize"/>): what the offline comparison runs the candidate with, through the same code as the reference.
    /// </summary>
    public BiometricModelOptions ForAntiSpoofingCandidate()
    {
        var copy = (BiometricModelOptions)MemberwiseClone();
        copy.LivenessCropScale = AntiSpoofingCandidate.CropScale;
        copy.LivenessInputSize = AntiSpoofingCandidate.InputSize;
        return copy;
    }

    /// <summary>
    /// A copy of these settings with the normalization of the recognition candidate (<see cref="FaceRecognitionCandidateOptions"/>: size, mean, divisor and colour order) in place of the
    /// reference's, for the offline comparison.
    /// </summary>
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

/// <summary>
/// The optional second candidate for the anti-spoofing model (<c>Biometrics:AntiSpoofingCandidate:*</c>, E72/F01/US02/T03): the file (<see cref="ModelFileOptions"/>) and how a face is cut for
/// it. The defaults are the ones of the reference model (MiniFASNetV2): a candidate with another crop is described by configuration. Empty file settings mean not configured.
/// </summary>
public class AntiSpoofingCandidateOptions : ModelFileOptions
{
    /// <summary>How much the face box is enlarged before it is cut for the candidate (1 to 8). Default 2.7, the reference's.</summary>
    public double CropScale { get; set; } = 2.7;

    /// <summary>Side of the square cut given to the candidate, in pixels (16 to 512). Default 80, the reference's.</summary>
    public int InputSize { get; set; } = 80;
}

/// <summary>
/// The optional second candidate for the recognition model (<c>Biometrics:FaceRecognitionCandidate:*</c>, E72/F01/US02/T03): the file (<see cref="ModelFileOptions"/>) and the normalization the
/// candidate was trained with, like the options of AuraFace (<see cref="BiometricModelOptions.RecognitionMean"/> and the following): a candidate with another normalization is described by
/// configuration. The defaults are the ones of the reference model (SFace). Empty file settings mean not configured.
/// </summary>
public class FaceRecognitionCandidateOptions : ModelFileOptions
{
    /// <summary>Side of the aligned face given to the candidate, in pixels (32 to 512), when the model does not fix its own input size; one that does wins. Default 112.</summary>
    public int InputSize { get; set; } = 112;

    /// <summary>Value taken from every pixel before the candidate (127.5 for the models of the ArcFace family, 0 for SFace). Default 0.</summary>
    public double Mean { get; set; }

    /// <summary>Divisor of every pixel after the mean (above 0; 127.5 for the models of the ArcFace family, 1 for SFace). Default 1.</summary>
    public double Std { get; set; } = 1;

    /// <summary>True when the candidate wants red, green, blue; false for blue, green, red. Default true.</summary>
    public bool RgbOrder { get; set; } = true;
}
