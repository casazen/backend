using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

public enum CandidateCaseKind
{
    Match,

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

    public string Id { get; }

    public CandidateCaseKind Kind { get; }
}

public sealed class MatchCase : CandidateCase
{
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

    public RasterImage DocumentFront { get; }

    public IReadOnlyList<RasterImage> Frames { get; }
}

public sealed class LivenessCase : CandidateCase
{
    public LivenessCase(string id, byte[] encodedFrame)
        : base(id, CandidateCaseKind.Liveness)
    {
        ArgumentNullException.ThrowIfNull(encodedFrame);
        if (encodedFrame.Length == 0)
            throw new ArgumentException("A liveness case needs a picture.", nameof(encodedFrame));

        EncodedFrame = encodedFrame;
    }

    public byte[] EncodedFrame { get; }
}

public static class CandidateRunCodes
{
    public const string Unavailable = "biometrics_unavailable";

    public const string NoFace = "no_face";

    public const string NotScored = "not_scored";

    public const string Error = "error";

    public const string CandidateLoading = "candidate_loading";
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
    public bool Ran => FailureCode is null;
}

/// <summary>The reference and the candidate on the same case.</summary>
/// <param name="CaseId">The label of the case.</param>
/// <param name="Kind">Which models the case was for.</param>
/// <param name="Reference">The reference model (SFace for a match, MiniFASNetV2 for a liveness) as the check-in runs it.</param>
/// <param name="Candidate">
/// The candidate of the same role on the same inputs. <b>Three answers, and the runner tells them apart:</b> a run that ran (its scores); <c>null</c> when the candidate is not configured, or it failed (the
/// <c>Candidates</c> of the snapshot of the store say which, and why); and a run with no scores and the code <see cref="CandidateRunCodes.CandidateLoading"/> when the candidate is configured but the store has
/// not looked at it yet (the store is ready as soon as the three models are, and loads the candidates after them): the runner waits and compares again, and never records that case as «no candidate».
/// </param>
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
    /// order; a case that fails is a result with a code, never an exception that stops the others. A candidate that is configured but not yet looked at by the store is a run with the code
    /// <see cref="CandidateRunCodes.CandidateLoading"/>, not a missing one (<see cref="CandidateCaseResult.Candidate"/>). A caller that cancels gets the cancellation.
    /// </summary>
    Task<IReadOnlyList<CandidateCaseResult>> CompareAsync(IReadOnlyList<CandidateCase> cases, CancellationToken ct);
}
