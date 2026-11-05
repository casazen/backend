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

    /// <summary>
    /// Face recognition (AuraFace-v1, Apache-2.0, ~260 MB): over 100 MB, so external: downloaded from <see cref="ModelFileOptions.DownloadUrl"/> and
    /// verified by SHA-256 (ADR-013-R8). SFace (OpenCV Zoo, Apache-2.0, 128 values, <see cref="RecognitionMean"/> 0 and <see cref="RecognitionStd"/> 1)
    /// is the documented fallback: only these settings change.
    /// </summary>
    public ModelFileOptions FaceRecognition { get; set; } = new()
    {
        Name = "AuraFace-v1",
        Version = "1.0.0",
        License = "Apache-2.0",
        Provenance = "Trained by its authors on a dataset licensed for commercial use; to be confirmed in the DPIA (ADR-013 gate).",
        Path = "MlModels/biometrics/glintr100.onnx",
    };

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
    public double RecognitionMean { get; set; } = 127.5;

    /// <summary>Divisor of every pixel after the mean (127.5 for AuraFace and ArcFace models, 1 for SFace).</summary>
    public double RecognitionStd { get; set; } = 127.5;

    /// <summary>True when the recognition model wants red, green, blue (all the models above); false for blue, green, red.</summary>
    public bool RecognitionRgbOrder { get; set; } = true;

    /// <summary>Time one verification may take, in milliseconds, liveness included (ADR-013-R1). Past it the answer is the operator path.</summary>
    public int VerificationTimeoutMs { get; set; } = DefaultVerificationTimeoutMs;

    /// <summary>Time the liveness check alone may take, in milliseconds.</summary>
    public int LivenessTimeoutMs { get; set; } = 1500;

    /// <summary>Minutes a model download may take before it is given up.</summary>
    public int DownloadTimeoutMinutes { get; set; } = 30;

    /// <summary>Threads of one model run; 0 lets ONNX Runtime decide.</summary>
    public int IntraOpThreads { get; set; }

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

    private static void CheckFraction(List<string> failures, string name, double value)
    {
        if (double.IsNaN(value) || value <= 0 || value >= 1)
            failures.Add($"{SectionName}:{name} must be between 0 and 1 (exclusive).");
    }
}
