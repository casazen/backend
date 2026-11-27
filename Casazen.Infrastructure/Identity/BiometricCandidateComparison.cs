using System.Diagnostics;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The comparison of the optional second candidates with the reference models on the same cases (E72/F01/US02/T03, decision D6): see <see cref="IBiometricCandidateComparison"/>. The reference is the
/// model of the check-in run the way the check-in runs it: SFace through the embedder of the check-in and MiniFASNetV2 through <see cref="AntiSpoofingService"/>, over the real store. The candidate is
/// run through <b>the same classes</b>, over a store that holds only the candidate's model and with the options of the candidate (the way it wants a face cut, its normalization), so the only
/// difference between the two is the model and what describes it. The faces are found once, by the detector of the check-in, and both models are given the same ones.
/// <para>
/// <b>It decides nothing and nothing depends on it.</b> It is registered for the qualification runner and never injected into the flow; it reads the candidates the store loaded and has no way to load one.
/// With no candidate loaded the answer is the reference alone, except for a candidate that is configured but not yet looked at by the store (it loads them after the three models): that one is a run with the code
/// <see cref="CandidateRunCodes.CandidateLoading"/>, so the runner can tell it from a candidate that is not there. It has no logger (the numbers of a case are the result, not a log), the vectors are zeroed after each comparison, and a case that fails
/// is a code in its result, never the message of an exception.
/// </para>
/// </summary>
public sealed class BiometricCandidateComparison(
    IBiometricModelStore store,
    IFaceDetector detector,
    IFaceEmbedder referenceEmbedder,
    IOptions<BiometricModelOptions> options) : IBiometricCandidateComparison
{
    /// <summary>
    /// The models of a comparison, read from the store once for the whole call: the reference, the candidate of each role when it is loaded and, for a role whose candidate is configured but not yet looked at by
    /// the store, the note that it is still loading (<see cref="CandidateRunCodes.CandidateLoading"/>).
    /// </summary>
    private sealed record Rig(
        IFaceEmbedder ReferenceEmbedder,
        IFaceEmbedder? CandidateEmbedder,
        IAntiSpoofingService ReferenceLiveness,
        IAntiSpoofingService? CandidateLiveness,
        bool RecognitionCandidateLoading,
        bool AntiSpoofingCandidateLoading);

    public async Task<IReadOnlyList<CandidateCaseResult>> CompareAsync(IReadOnlyList<CandidateCase> cases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Any(candidateCase => candidateCase is null))
            throw new ArgumentException("A case is missing.", nameof(cases));

        var settings = options.Value;
        var rig = BuildRig(settings);
        var results = new List<CandidateCaseResult>(cases.Count);
        foreach (var candidateCase in cases)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(candidateCase switch
            {
                MatchCase match => CompareMatch(match, rig, settings, ct),
                LivenessCase liveness => await CompareLivenessAsync(liveness, rig, ct),
                _ => throw new ArgumentException("The kind of case is not known.", nameof(cases)),
            });
        }

        return results;
    }

    private Rig BuildRig(BiometricModelOptions settings)
    {
        // The snapshot before the candidates: the store publishes them together, so a role that is not in the snapshot is one the store has not looked at yet, never one that was loaded a moment ago.
        var examined = store.Snapshot.Candidates;
        var recognition = store.FaceRecognitionCandidate;
        var antiSpoofing = store.AntiSpoofingCandidate;

        IFaceEmbedder? candidateEmbedder = null;
        if (recognition is not null)
            candidateEmbedder = new FaceEmbedder(new CandidateModelStore(antiSpoofing: null, recognition), Options.Create(settings.ForFaceRecognitionCandidate()));

        IAntiSpoofingService? candidateLiveness = null;
        if (antiSpoofing is not null)
        {
            candidateLiveness = new AntiSpoofingService(
                NullLogger<AntiSpoofingService>.Instance,
                new CandidateModelStore(antiSpoofing, recognition: null),
                detector,
                Options.Create(settings.ForAntiSpoofingCandidate()));
        }

        // The service of the check-in over the real store: the reference is what the check-in runs, not a copy of it.
        var referenceLiveness = new AntiSpoofingService(NullLogger<AntiSpoofingService>.Instance, store, detector, options);
        var recognitionLoading = recognition is null && IsLoading(BiometricModelRoles.FaceRecognitionCandidate, settings.FaceRecognitionCandidate, examined);
        var antiSpoofingLoading = antiSpoofing is null && IsLoading(BiometricModelRoles.AntiSpoofingCandidate, settings.AntiSpoofingCandidate, examined);
        return new Rig(referenceEmbedder, candidateEmbedder, referenceLiveness, candidateLiveness, recognitionLoading, antiSpoofingLoading);
    }

    /// <summary>
    /// Whether a candidate is configured and the store has not looked at it yet: the store is ready once the three models are loaded and loads the candidates after them, so for a moment the role is configured, has
    /// no model and is not in <c>Candidates</c> either. A role the store did look at (not configured, or failed with its code) is not loading: it is a candidate that is not there.
    /// </summary>
    private static bool IsLoading(string role, ModelFileOptions spec, IReadOnlyList<BiometricModelInfo> examined)
    {
        return BiometricModelOptions.IsCandidateConfigured(spec) && !examined.Any(info => string.Equals(info.Role, role, StringComparison.Ordinal));
    }

    // ───── a match: the portrait of the document against the chosen frames ─────

    private CandidateCaseResult CompareMatch(MatchCase match, Rig rig, BiometricModelOptions settings, CancellationToken ct)
    {
        var reference = store.FaceRecognition;
        var candidate = store.FaceRecognitionCandidate;
        if (!store.IsAvailable || reference is null)
        {
            return new CandidateCaseResult(match.Id, match.Kind, Failed(BiometricModelRoles.FaceRecognition, reference, settings.FaceRecognition, CandidateRunCodes.Unavailable), null);
        }

        RasterImage? rotatedDocument = null;
        try
        {
            // The faces are found once, and both models are given the same ones.
            var portrait = DocumentPortrait.Find(detector, match.DocumentFront, settings, ct, out rotatedDocument);
            var faces = match.Frames.Select(frame => detector.Detect(frame, settings.DetectionScoreThreshold, ct).FirstOrDefault()).ToList();
            var portraitImage = rotatedDocument ?? match.DocumentFront;

            var referenceRun = portrait is null
                ? Failed(BiometricModelRoles.FaceRecognition, reference, settings.FaceRecognition, CandidateRunCodes.NoFace)
                : Embed(BiometricModelRoles.FaceRecognition, reference, rig.ReferenceEmbedder, portraitImage, portrait, match.Frames, faces, ct);
            CandidateModelRun? candidateRun = null;
            if (rig.CandidateEmbedder is not null && candidate is not null)
            {
                candidateRun = portrait is null
                    ? Failed(BiometricModelRoles.FaceRecognitionCandidate, candidate, settings.FaceRecognitionCandidate, CandidateRunCodes.NoFace)
                    : Embed(BiometricModelRoles.FaceRecognitionCandidate, candidate, rig.CandidateEmbedder, portraitImage, portrait, match.Frames, faces, ct);
            }
            else if (rig.RecognitionCandidateLoading)
            {
                candidateRun = Failed(BiometricModelRoles.FaceRecognitionCandidate, null, settings.FaceRecognitionCandidate, CandidateRunCodes.CandidateLoading);
            }

            return new CandidateCaseResult(match.Id, match.Kind, referenceRun, candidateRun);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BiometricUnavailableException)
        {
            return new CandidateCaseResult(match.Id, match.Kind, Failed(BiometricModelRoles.FaceRecognition, reference, settings.FaceRecognition, CandidateRunCodes.Unavailable), null);
        }
        catch (Exception)
        {
            // Nothing of the exception: a message could carry what was being read.
            return new CandidateCaseResult(match.Id, match.Kind, Failed(BiometricModelRoles.FaceRecognition, reference, settings.FaceRecognition, CandidateRunCodes.Error), null);
        }
        finally
        {
            rotatedDocument?.Dispose();
        }
    }

    /// <summary>
    /// The similarity of the portrait with each frame by <paramref name="embedder"/>, on the scale of the check-in (the cosine of the two vectors clamped to 0..1; a frame with no face is 0), and the time
    /// the embedder took (the vectors, not the faces both models share). A failure of this model is its own code and does not stop the other.
    /// </summary>
    private static CandidateModelRun Embed(
        string role,
        LoadedBiometricModel model,
        IFaceEmbedder embedder,
        RasterImage portraitImage,
        DetectedFace portrait,
        IReadOnlyList<RasterImage> frames,
        IReadOnlyList<DetectedFace?> faces,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        float[]? portraitVector = null;
        var scores = new List<double>(frames.Count);
        try
        {
            portraitVector = embedder.Embed(portraitImage, portrait, ct);
            for (var index = 0; index < frames.Count; index++)
            {
                if (faces[index] is not { } face)
                {
                    scores.Add(0);
                    continue;
                }

                var vector = embedder.Embed(frames[index], face, ct);
                try
                {
                    var similarity = Math.Clamp(EmbeddingMath.CosineSimilarity(portraitVector, vector), 0.0, 1.0);
                    scores.Add(double.IsFinite(similarity) ? similarity : 0);
                }
                finally
                {
                    ImageBytes.Zero(vector);
                }
            }

            return new CandidateModelRun(role, model.Name, model.Version, model.WeightHash, scores, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BiometricUnavailableException)
        {
            return Failed(role, model, CandidateRunCodes.Unavailable);
        }
        catch (Exception)
        {
            return Failed(role, model, CandidateRunCodes.Error);
        }
        finally
        {
            ImageBytes.Zero(portraitVector);
        }
    }

    // ───── a liveness: one frame ─────

    private async Task<CandidateCaseResult> CompareLivenessAsync(LivenessCase liveness, Rig rig, CancellationToken ct)
    {
        var settings = options.Value;
        var reference = store.AntiSpoofing;
        var candidate = store.AntiSpoofingCandidate;
        if (!store.IsAvailable || reference is null)
            return new CandidateCaseResult(liveness.Id, liveness.Kind, Failed(BiometricModelRoles.AntiSpoofing, reference, settings.AntiSpoofing, CandidateRunCodes.Unavailable), null);

        var referenceRun = await ScoreAsync(BiometricModelRoles.AntiSpoofing, reference, rig.ReferenceLiveness, liveness.EncodedFrame, ct);
        CandidateModelRun? candidateRun = null;
        if (rig.CandidateLiveness is not null && candidate is not null)
            candidateRun = await ScoreAsync(BiometricModelRoles.AntiSpoofingCandidate, candidate, rig.CandidateLiveness, liveness.EncodedFrame, ct);
        else if (rig.AntiSpoofingCandidateLoading)
            candidateRun = Failed(BiometricModelRoles.AntiSpoofingCandidate, null, settings.AntiSpoofingCandidate, CandidateRunCodes.CandidateLoading);

        return new CandidateCaseResult(liveness.Id, liveness.Kind, referenceRun, candidateRun);
    }

    /// <summary>
    /// The score of the real-face class of one frame by <paramref name="service"/>, and the time the whole reading took (decoding, the face detector, the cut and the model: the part the two models
    /// share is in both). The service says «not live, score 0» for everything that is not a score, which is <see cref="CandidateRunCodes.NotScored"/> here.
    /// </summary>
    private static async Task<CandidateModelRun> ScoreAsync(string role, LoadedBiometricModel model, IAntiSpoofingService service, byte[] encodedFrame, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var picture = new MemoryStream(encodedFrame, writable: false);
            var result = await service.ScoreLivenessAsync(picture, ct);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!double.IsFinite(result.Score) || result.Score <= 0 || result.Score > 1)
                return Failed(role, model, CandidateRunCodes.NotScored);

            return new CandidateModelRun(role, model.Name, model.Version, model.WeightHash, [result.Score], elapsed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed(role, model, CandidateRunCodes.Error);
        }
    }

    /// <summary>A run that gave no scores: who it was (from the model when it is loaded, else from the settings) and why.</summary>
    private static CandidateModelRun Failed(string role, LoadedBiometricModel? model, ModelFileOptions settings, string code)
    {
        return model is null
            ? new CandidateModelRun(role, settings.Name, settings.Version, null, [], 0, code)
            : Failed(role, model, code);
    }

    private static CandidateModelRun Failed(string role, LoadedBiometricModel model, string code)
    {
        return new CandidateModelRun(role, model.Name, model.Version, model.WeightHash, [], 0, code);
    }

    /// <summary>
    /// A store that holds one model, the candidate: what the classes of the check-in are given so that they run it exactly as they run the reference. It is always available (the candidate was
    /// verified and loaded by the real store), and it holds no other model: a class that asks for one it does not hold gets none and answers «not available».
    /// </summary>
    internal sealed class CandidateModelStore(LoadedBiometricModel? antiSpoofing, LoadedBiometricModel? recognition) : IBiometricModelStore
    {
        public BiometricModelsSnapshot Snapshot { get; } = new(BiometricModelsState.Ready, null, []);

        public bool IsAvailable => true;

        public LoadedBiometricModel? FaceDetector => null;

        public LoadedBiometricModel? AntiSpoofing { get; } = antiSpoofing;

        public LoadedBiometricModel? FaceRecognition { get; } = recognition;
    }
}
