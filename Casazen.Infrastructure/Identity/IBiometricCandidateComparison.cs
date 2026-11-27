using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>What a case of the comparison of a candidate model with the reference asks of the models.</summary>
public enum CandidateCaseKind
{
    /// <summary>The portrait of a document against the frames chosen from a presence sequence: the recognition model (the reference SFace and the recognition candidate).</summary>
    Match,

    /// <summary>One frame of a presence sequence: the anti-spoofing model (the reference MiniFASNetV2 and the anti-spoofing candidate).</summary>
    Liveness,
}

/// <summary>
/// One input of the comparison (E72/F01/US02/T03, decision D6): a case is given to the reference model and to the candidate, which see exactly the same pictures. The case holds the pictures it
/// is given and owns none of them: the caller, the offline qualification runner, keeps and disposes them. An identifier is a label of the qualification (never a name, a document number or
/// anything about a person), and comes back in the result.
/// </summary>
public abstract class CandidateCase
{
    private protected CandidateCase(string id, CandidateCaseKind kind)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A case needs an identifier.", nameof(id));

        Id = id;
        Kind = kind;
    }

    /// <summary>The label of the case in the report of the qualification.</summary>
    public string Id { get; }

    /// <summary>Which models the case is for.</summary>
    public CandidateCaseKind Kind { get; }
}

/// <summary>A document portrait and the frames compared with it: what the recognition model is asked (the same comparison as <see cref="PresenceMatcher"/>, one similarity per frame).</summary>
public sealed class MatchCase : CandidateCase
{
    /// <summary>The case of <paramref name="documentFront"/> against <paramref name="frames"/>, which are the pictures the server chose from a sequence.</summary>
    public MatchCase(string id, RasterImage documentFront, IReadOnlyList<RasterImage> frames)
        : base(id, CandidateCaseKind.Match)
    {
        ArgumentNullException.ThrowIfNull(documentFront);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0 || frames.Any(frame => frame is null))
            throw new ArgumentException("A match case needs at least one frame.", nameof(frames));

        DocumentFront = documentFront;
        Frames = frames;
    }

    /// <summary>The front of the document, where the portrait is found.</summary>
    public RasterImage DocumentFront { get; }

    /// <summary>The frames compared with the portrait, in order.</summary>
    public IReadOnlyList<RasterImage> Frames { get; }
}

/// <summary>One frame, as the JPEG or PNG bytes of the picture: what the anti-spoofing model is asked (the same reading as <see cref="SequenceLivenessEvaluator"/> gives the service of the check-in).</summary>
public sealed class LivenessCase : CandidateCase
{
    /// <summary>The case of the frame <paramref name="encodedFrame"/> (the encoded bytes, as <see cref="SequenceFrame.Jpeg"/> keeps them).</summary>
    public LivenessCase(string id, byte[] encodedFrame)
        : base(id, CandidateCaseKind.Liveness)
    {
        ArgumentNullException.ThrowIfNull(encodedFrame);
        if (encodedFrame.Length == 0)
            throw new ArgumentException("A liveness case needs a picture.", nameof(encodedFrame));

        EncodedFrame = encodedFrame;
    }

    /// <summary>The bytes of the frame, read and never changed.</summary>
    public byte[] EncodedFrame { get; }
}

/// <summary>The codes of a run of a model that did not give scores (<see cref="CandidateModelRun.FailureCode"/>): codes only, never a message.</summary>
public static class CandidateRunCodes
{
    /// <summary>The reference models are not available: nothing could be compared.</summary>
    public const string Unavailable = "biometrics_unavailable";

    /// <summary>No face in the portrait of the document, or in the frame of a liveness case.</summary>
    public const string NoFace = "no_face";

    /// <summary>The model gave no score: it did not run, it found nothing to cut, or its answer was not one (a score of 0 is what a model that did not run gives).</summary>
    public const string NotScored = "not_scored";

    /// <summary>The run failed (the type of the failure is not told: a message could carry what was being read).</summary>
    public const string Error = "error";
}

/// <summary>
/// What one model made of a case: the scores it gave, how long it took, and who it is (the record of the qualification: name, version and the SHA-256 of the weights that were proved).
/// Numbers of a test case: returned to the runner, never logged.
/// </summary>
/// <param name="Role">The role of the model, a code of <see cref="BiometricModelRoles"/>.</param>
/// <param name="Name">The name of the model.</param>
/// <param name="Version">The version of the model.</param>
/// <param name="WeightHash">The SHA-256 of the weights that were verified (uppercase hexadecimal); null when the model is not loaded.</param>
/// <param name="Scores">
/// For a <see cref="CandidateCaseKind.Match"/> case, the similarity of each frame with the portrait (0 to 1, the scale of the check-in; 0 for a frame with no face), in the order of the frames; for a
/// <see cref="CandidateCaseKind.Liveness"/> case, the one score of the real-face class (above 0, at most 1). Empty when the model did not run.
/// </param>
/// <param name="LatencyMs">The time the model took on the case, in milliseconds, without what the two models share. For a match, the vectors of the portrait and the frames; for a liveness, the whole reading of the frame (decoding, the face detector, the cut and the model), which both share.</param>
/// <param name="FailureCode">Why there are no scores, a code of <see cref="CandidateRunCodes"/>; null when the model ran.</param>
public sealed record CandidateModelRun(string Role, string Name, string Version, string? WeightHash, IReadOnlyList<double> Scores, double LatencyMs, string? FailureCode)
{
    /// <summary>True when the model ran and has its scores.</summary>
    public bool Ran => FailureCode is null;
}

/// <summary>The reference and the candidate on the same case.</summary>
/// <param name="CaseId">The label of the case.</param>
/// <param name="Kind">Which models the case was for.</param>
/// <param name="Reference">The reference model (SFace for a match, MiniFASNetV2 for a liveness) as the check-in runs it.</param>
/// <param name="Candidate">The candidate of the same role on the same inputs; null when that candidate is not loaded (not configured, or it failed: the snapshot of the store says why).</param>
public sealed record CandidateCaseResult(string CaseId, CandidateCaseKind Kind, CandidateModelRun Reference, CandidateModelRun? Candidate);

/// <summary>
/// The comparison of the optional second candidates with the reference models on the same cases (E72/F01/US02/T03, decision D6, the spec «Qualificazione dei modelli»): the plug-in point of the
/// qualification, and nothing more. <b>It is used only by the offline qualification runner (E74); the live flow does not depend on it</b> (a test proves that the verification flow
/// takes nothing that takes it), a candidate never decides, and no weight file of a candidate is in the repository.
/// </summary>
public interface IBiometricCandidateComparison
{
    /// <summary>
    /// Runs every case through the reference model and, where one is loaded, through the candidate of the same role, on the same inputs, with the latency of each. One result per case, in
    /// order; a case that fails is a result with a code, never an exception that stops the others. A caller that cancels gets the cancellation.
    /// </summary>
    Task<IReadOnlyList<CandidateCaseResult>> CompareAsync(IReadOnlyList<CandidateCase> cases, CancellationToken ct);
}
