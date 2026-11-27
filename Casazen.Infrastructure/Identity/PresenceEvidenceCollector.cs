using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// What the pipeline of the presence found for one attempt (E73/F01/US01/T02, NF-192, NF-193): the validation of the sequence, why the server could not choose the frames (when it
/// could not), the liveness of the frames it chose and their comparison with the portrait of the document. A step that was not reached is null: a failed validation stops before the
/// choice of the frames, a failed choice before the liveness, a liveness that is not a pass before the match (a sequence that is not live is not compared with the document). The
/// numbers in it are of a person: they go to the decision and to its log row, never to a log of the application.
/// </summary>
/// <param name="Validation">The validation of the sequence (timing, faces, quality, continuity, challenge).</param>
/// <param name="SelectionFailure">Why the frames could not be chosen (<c>presence_quality</c>, correctable); null when they were, or when the validation did not pass.</param>
/// <param name="Liveness">The liveness of the chosen frames; null when it was not reached.</param>
/// <param name="Match">The comparison with the portrait; null when it was not reached.</param>
/// <param name="Selection">
/// The frames the server chose (places in the sequence, never pixels): what the evidence of the reviewer (E73/F01/US02/T01) may keep; null when they were not chosen.
/// </param>
public sealed record PresenceEvidence(
    SequenceValidationReport Validation,
    SequenceFailure? SelectionFailure,
    LivenessReport? Liveness,
    MatchReport? Match,
    SelectedFrames? Selection = null)
{
    private static readonly string[] ValidationChecks =
    [
        VerificationReasons.Checks.PresenceTiming,
        VerificationReasons.Checks.PresenceSingleFace,
        VerificationReasons.Checks.PresenceQuality,
        VerificationReasons.Checks.PresenceContinuity,
        VerificationReasons.Checks.PresenceChallenge,
    ];

    /// <summary>
    /// <see cref="IdentityModelsFault.Failed"/> when a step could not be judged (the validation, the liveness or the match was not available: a model that is not there, failed or ran
    /// out of its time), <see cref="IdentityModelsFault.None"/> otherwise.
    /// </summary>
    public IdentityModelsFault ModelsFault =>
        Validation.IsUnavailable || Liveness?.IsUnavailable == true || Match?.State == MatchState.Unavailable
            ? IdentityModelsFault.Failed
            : IdentityModelsFault.None;

    /// <summary>
    /// The checks of the presence as the decision reads them, one per check of <see cref="IdentityEvidenceBundle.PresenceChecks"/>: a check of the validation is failed when the
    /// validation (or the choice of the frames) found something for it, correctable only when every failure of it is, with the first guidance code; all of them are unavailable when
    /// the sequence could not be judged. The liveness is passed or failed (never correctable: a new recording does not make a picture of a screen live) with the lowest score of
    /// the chosen frames and the minimum of <paramref name="policy"/>, and not run when it was not reached.
    /// </summary>
    public IReadOnlyList<CheckOutcome> Checks(IdentityDecisionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var checks = new List<CheckOutcome>(ValidationChecks.Length + 1);
        var failures = new List<SequenceFailure>(Validation.Failures);
        if (SelectionFailure is not null)
            failures.Add(SelectionFailure);

        foreach (var check in ValidationChecks)
        {
            if (Validation.IsUnavailable)
            {
                checks.Add(new CheckOutcome(check, IdentityCheckStatus.Unavailable));
                continue;
            }

            var found = failures.Where(failure => string.Equals(failure.Check, check, StringComparison.Ordinal)).ToList();
            checks.Add(found.Count == 0
                ? new CheckOutcome(check, IdentityCheckStatus.Passed)
                : new CheckOutcome(
                    check,
                    IdentityCheckStatus.Failed,
                    found.All(failure => failure.Correctable),
                    found.Select(failure => failure.Code).FirstOrDefault(code => !string.IsNullOrEmpty(code))));
        }

        var minScore = policy.Presence.Liveness.MinScore;
        checks.Add(Liveness switch
        {
            null => new CheckOutcome(VerificationReasons.Checks.Liveness, IdentityCheckStatus.NotRun),
            { State: LivenessState.Passed } passed => new CheckOutcome(VerificationReasons.Checks.Liveness, IdentityCheckStatus.Passed, Value: passed.MinScore, Threshold: minScore),
            { State: LivenessState.Failed } failed => new CheckOutcome(VerificationReasons.Checks.Liveness, IdentityCheckStatus.Failed, Value: failed.MinScore, Threshold: minScore),
            _ => new CheckOutcome(VerificationReasons.Checks.Liveness, IdentityCheckStatus.Unavailable),
        });
        return checks;
    }
}

/// <summary>
/// The pipeline of the presence of one attempt (E73/F01/US01/T02): the seam between the closure and the pieces of E72, so that the closure (the lock, the replay, the policy and the
/// one save) is decided on what they found, and a double of them can stand in for the models in the tests of the closure.
/// </summary>
public interface IPresenceEvidenceCollector
{
    /// <summary>
    /// Collects the evidence of <paramref name="sequence"/> (read in memory by <see cref="FrameSequenceReader"/>, owned and disposed by the caller) for <paramref name="challenge"/>,
    /// with the picture of the front of the document <paramref name="documentFront"/> (owned and zeroed by the caller) and <paramref name="policy"/>. A step that could not be
    /// judged is in the evidence as not available, never as a pass; a cancellation of <paramref name="ct"/> (the request, or the time of the attempt) is an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<PresenceEvidence> CollectAsync(
        FrameSequence sequence,
        StoredPresenceChallenge challenge,
        byte[] documentFront,
        IdentityDecisionPolicy policy,
        CancellationToken ct);
}

/// <summary>
/// The pipeline of the presence with the pieces of E72 (NF-192, NF-193, ADR-013-R17, decisions D6 and D7): the validation of the sequence (<see cref="PresenceSequenceValidator"/>),
/// the frames chosen by the server (<see cref="PresenceFrameSelector"/>), their liveness (<see cref="SequenceLivenessEvaluator"/>, MiniFASNetV2 on every chosen frame) and the comparison
/// of the portrait of the document with them (<see cref="PresenceMatcher"/>, SFace with the frozen aggregation rule). It decides nothing: the decision is the engine's.
/// <para>
/// <b>Privacy</b> (ADR-013-R2, R17): the frames and the document are the caller's; the only copy made here is the decoded picture of the document, zeroed before this returns. No
/// logger: the pieces log their codes and counts, never a number of a person.
/// </para>
/// </summary>
public sealed class PresenceEvidenceCollector(
    PresenceSequenceValidator validator,
    PresenceFrameSelector selector,
    SequenceLivenessEvaluator liveness,
    PresenceMatcher matcher,
    IOptions<BiometricModelOptions> biometricOptions) : IPresenceEvidenceCollector
{
    public async Task<PresenceEvidence> CollectAsync(
        FrameSequence sequence,
        StoredPresenceChallenge challenge,
        byte[] documentFront,
        IdentityDecisionPolicy policy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(documentFront);
        ArgumentNullException.ThrowIfNull(policy);

        var validation = await validator.ValidateAsync(sequence, challenge, policy, ct);
        if (!validation.Passed)
            return new PresenceEvidence(validation, null, null, null);

        var selection = selector.Select(sequence, validation, policy);
        if (!selection.IsSelected)
            return new PresenceEvidence(validation, selection.Failure, null, null);

        var chosen = selection.Frames;
        var live = await liveness.EvaluateAsync(sequence, chosen.LivenessIndices, policy, ct);
        if (!live.Passed)
            return new PresenceEvidence(validation, null, live, null, chosen);

        RasterImage document;
        try
        {
            document = RasterImage.Decode(documentFront, biometricOptions.Value.MaxImagePixels);
        }
        catch (InvalidDataException)
        {
            return new PresenceEvidence(validation, null, live, MatchReport.NotAvailable(), chosen);
        }

        using (document)
        {
            var match = await matcher.MatchAsync(document, sequence, chosen.MatchIndices, policy, ct);
            return new PresenceEvidence(validation, null, live, match, chosen);
        }
    }
}
