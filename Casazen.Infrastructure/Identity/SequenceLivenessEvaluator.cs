using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

public enum LivenessState
{
    Passed,

    Failed,

    Unavailable,
}

/// <summary>The score of the real-face class the anti-spoofing model gave one chosen frame (a number of a person: in memory and in the log of the decision only, never in a log of the application).</summary>
/// <param name="Index">The place of the frame in the sequence.</param>
/// <param name="Score">The score, above 0 and at most 1 (0 is never a score: it is what a model that did not run gives).</param>
public sealed record FrameLiveness(int Index, double Score);

public sealed record LivenessReport(LivenessState State, IReadOnlyList<FrameLiveness> Frames)
{
    public bool Passed => State == LivenessState.Passed;

    public bool IsUnavailable => State == LivenessState.Unavailable;

    public double? MinScore => Frames.Count == 0 ? null : Frames.Min(frame => frame.Score);

    public static LivenessReport NotAvailable()
    {
        return new LivenessReport(LivenessState.Unavailable, []);
    }
}

/// <summary>
/// Judges the liveness of the frames the server chose from a validated presence sequence (E72/F01/US02/T01, NF-192, NF-193, ADR-013-R17, decision D6: MiniFASNetV2 is the
/// reference). Every chosen frame goes through the anti-spoofing service of the check-in (<see cref="IAntiSpoofingService"/>: it finds the face of the frame, cuts it with the
/// crop scale and the input size of <see cref="BiometricModelOptions"/> and takes the score of the real-face class), and the rule of the policy decides, not the threshold of the
/// service: <c>presence.liveness.rule</c> <c>all</c> means every chosen frame must reach <c>presence.liveness.minScore</c>. Every frame is scored, so the report always has the
/// scores of all of them (unless it is unavailable).
/// <para>
/// <b>Fails closed.</b> A model that did not run says so with a result the service documents (<c>default</c>: not live, score 0): no policy, a rule the code does not know, a model that is
/// not ready, a frame the model could not score, an exception or a time over (<c>Biometrics:LivenessTimeoutMs</c> for each frame, and that times the number of frames for all of them)
/// is <see cref="LivenessState.Unavailable"/>, never <see cref="LivenessState.Passed"/>; a caller that cancels gets the cancellation. A score of exactly 0 is taken as «not scored»
/// too: it cannot be told from a model that did not run, and it is not a pass either way.
/// </para>
/// <para>
/// <b>Privacy</b> (ADR-013-R2, R17): the JPEG bytes of the frames are the sequence's and are only read here (the service copies what it needs and zeroes its copy); nothing is
/// kept. The log has the number of frames and the state, never a score, a frame or the message of an exception.
/// </para>
/// </summary>
public sealed class SequenceLivenessEvaluator(
    IAntiSpoofingService antiSpoofing,
    IOptions<BiometricModelOptions> biometricOptions,
    ILogger<SequenceLivenessEvaluator> logger)
{
    public async Task<LivenessReport> EvaluateAsync(FrameSequence sequence, IReadOnlyList<int> indices, IdentityDecisionPolicy? policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(indices);
        RequireFrames(sequence, indices);
        ct.ThrowIfCancellationRequested();

        if (policy is null || !IdentityLivenessRules.All.Contains(policy.Presence.Liveness.Rule, StringComparer.Ordinal))
        {
            logger.LogWarning("The liveness of the sequence could not be judged: the policy or its rule is not available; a person decides");
            return LivenessReport.NotAvailable();
        }

        var rules = policy.Presence.Liveness;
        var timeoutMs = biometricOptions.Value.LivenessTimeoutMs;
        if (timeoutMs <= 0)
        {
            logger.LogWarning("The liveness of the sequence could not be judged: no time limit is configured; a person decides");
            return LivenessReport.NotAvailable();
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMilliseconds((double)timeoutMs * indices.Count));
        try
        {
            var scores = new List<FrameLiveness>(indices.Count);
            foreach (var index in indices)
            {
                budget.Token.ThrowIfCancellationRequested();

                using var picture = new MemoryStream(sequence.Frames[index].Jpeg, writable: false);
                var result = await antiSpoofing.ScoreLivenessAsync(picture, budget.Token);
                if (!double.IsFinite(result.Score) || result.Score <= 0 || result.Score > 1)
                {
                    logger.LogWarning("The liveness of the sequence could not be judged: the model did not score a frame; a person decides");
                    return LivenessReport.NotAvailable();
                }

                scores.Add(new FrameLiveness(index, result.Score));
            }

            var passed = scores.All(frame => frame.Score >= rules.MinScore);
            logger.LogInformation("Presence liveness judged: passed={Passed}, {Frames} frames", passed, scores.Count);
            return new LivenessReport(passed ? LivenessState.Passed : LivenessState.Failed, scores);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("The liveness of the sequence could not be judged in the time given; a person decides");
            return LivenessReport.NotAvailable();
        }
        catch (BiometricUnavailableException)
        {
            logger.LogWarning("The liveness of the sequence could not be judged: the models are not available; a person decides");
            return LivenessReport.NotAvailable();
        }
        catch (Exception ex)
        {
            logger.LogError("The liveness of the sequence could not be judged ({ErrorType}); a person decides", ex.GetType().Name);
            return LivenessReport.NotAvailable();
        }
    }

    private static void RequireFrames(FrameSequence sequence, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
            throw new ArgumentException("At least one frame is needed to judge the liveness.", nameof(indices));

        if (indices.Any(index => index < 0 || index >= sequence.Frames.Count))
            throw new ArgumentException("An index is not a frame of the sequence.", nameof(indices));

        if (indices.Distinct().Count() != indices.Count)
            throw new ArgumentException("A frame is named twice.", nameof(indices));
    }
}
