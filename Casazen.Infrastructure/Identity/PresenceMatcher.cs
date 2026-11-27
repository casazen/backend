using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Compares the portrait of the document with the frames the server chose from a validated presence sequence (E72/F01/US02/T02, NF-193, ADR-013-R17, decision D7): SFace, a
/// frozen aggregation rule and frozen thresholds, no compensation between presence and similarity. The portrait is found on the document front with
/// <c>DocumentPortrait.Find</c> (which tries the quarter and half turns of a document that lost its orientation), the face of each chosen frame is found with the face
/// detector of the check-in, and both are turned into vectors by the embedder of the check-in; the similarity of a frame is the cosine of the two vectors clamped to 0..1, the same
/// scale as <c>BiometricVerificationService</c>. The similarities are combined by <c>match.aggregation</c> (<c>median</c>, <c>min</c> or <c>mean</c>) and the report says whether the
/// aggregate reaches <c>match.accept</c> while every frame reaches <c>match.minEachFrame</c>, and whether the aggregate is in the review band.
/// <para>
/// <b>Independence is structural.</b> <see cref="MatchAsync"/> takes the document, the frames and the policy: no liveness, no challenge, no continuity, no report of the validation,
/// and the class holds none of those services. What it says cannot be raised or lowered by the presence: the AND of the independent thresholds is applied by the decision engine (E73).
/// A frame in which no face is found cannot match the portrait and counts as a similarity of 0 (so it fails <c>minEachFrame</c>).
/// </para>
/// <para>
/// <b>Fails closed.</b> A document with no face is <see cref="MatchState.NoPortrait"/>. No policy, a rule the code does not know, models that are not ready, a model that fails, or a time over
/// (<c>Biometrics:VerificationTimeoutMs</c>) is <see cref="MatchState.Unavailable"/>, never <see cref="MatchState.Ok"/>; a caller that cancels gets the cancellation. Where the document front comes
/// from (the browser, or, in Ticino, the document that came again with the sequence, kept nowhere) does not matter here: the caller owns it and zeroes it.
/// </para>
/// <para>
/// <b>Privacy</b> (ADR-013-R2, R17): the vector of the portrait, the vector of each frame and the rotated copy of the document are zeroed before this returns, on every path; the frames and
/// the document are the caller's. The log has the state and the number of frames, never a similarity, a vector or the message of an exception.
/// </para>
/// </summary>
public sealed class PresenceMatcher(
    IFaceDetector detector,
    IFaceEmbedder embedder,
    IOptions<BiometricModelOptions> biometricOptions,
    ILogger<PresenceMatcher> logger)
{
    /// <summary>
    /// Compares <paramref name="documentFront"/> with the frames of <paramref name="sequence"/> at <paramref name="matchIndices"/> (the <c>MatchIndices</c> the server chose) with
    /// <paramref name="policy"/>. A <c>null</c> policy is <see cref="MatchState.Unavailable"/>.
    /// </summary>
    /// <exception cref="ArgumentException">An index that is not a frame of the sequence or named twice, or a number of frames that is not <c>match.frames</c>.</exception>
    public Task<MatchReport> MatchAsync(
        RasterImage documentFront,
        FrameSequence sequence,
        IReadOnlyList<int> matchIndices,
        IdentityDecisionPolicy? policy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documentFront);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(matchIndices);
        RequireFrames(sequence, matchIndices);
        ct.ThrowIfCancellationRequested();

        if (policy is null || !IdentityAggregationRules.All.Contains(policy.Match.Aggregation, StringComparer.Ordinal))
        {
            logger.LogWarning("The match could not be made: the policy or its aggregation rule is not available; a person decides");
            return Task.FromResult(MatchReport.NotAvailable());
        }

        // The frozen rule is over the frames of the policy: a different number would be another rule.
        if (matchIndices.Count != policy.Match.Frames)
            throw new ArgumentException("The number of frames is not the one of the policy.", nameof(matchIndices));

        try
        {
            return Task.FromResult(Match(documentFront, sequence, matchIndices, policy.Match, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<MatchReport>(ct);
        }
    }

    private MatchReport Match(RasterImage documentFront, FrameSequence sequence, IReadOnlyList<int> indices, IdentityMatchPolicy rules, CancellationToken ct)
    {
        var settings = biometricOptions.Value;
        if (settings.VerificationTimeoutMs <= 0)
        {
            logger.LogWarning("The match could not be made: no time limit is configured; a person decides");
            return MatchReport.NotAvailable();
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(settings.VerificationTimeoutMs);
        try
        {
            var report = Compare(documentFront, sequence, indices, rules, settings, budget.Token);

            // The state and the number of frames only: never a similarity.
            logger.LogInformation("Presence match: state={State}, {Frames} frames", report.State, indices.Count);
            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("The match could not be made in {TimeoutMs} ms; a person decides", settings.VerificationTimeoutMs);
            return MatchReport.NotAvailable();
        }
        catch (BiometricUnavailableException)
        {
            logger.LogWarning("The match could not be made: the models are not available; a person decides");
            return MatchReport.NotAvailable();
        }
        catch (Exception ex)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("The match could not be made ({ErrorType}); a person decides", ex.GetType().Name);
            return MatchReport.NotAvailable();
        }
    }

    private MatchReport Compare(
        RasterImage documentFront,
        FrameSequence sequence,
        IReadOnlyList<int> indices,
        IdentityMatchPolicy rules,
        BiometricModelOptions settings,
        CancellationToken token)
    {
        float[]? portraitVector = null;
        RasterImage? rotatedDocument = null;
        var similarities = new List<double>(indices.Count);
        try
        {
            var portrait = DocumentPortrait.Find(detector, documentFront, settings, token, out rotatedDocument);
            if (portrait is null)
                return MatchReport.NoPortrait();

            portraitVector = embedder.Embed(rotatedDocument ?? documentFront, portrait, token);
            foreach (var index in indices)
            {
                token.ThrowIfCancellationRequested();
                similarities.Add(Similarity(portraitVector, sequence.Frames[index].Image, settings, token));
            }
        }
        finally
        {
            ImageBytes.Zero(portraitVector);
            rotatedDocument?.Dispose();
        }

        return Report(similarities, rules);
    }

    /// <summary>The similarity of the portrait and the face of one frame, in the scale of the check-in; 0 when the frame has no face. The vector of the frame is zeroed before this returns.</summary>
    private double Similarity(float[] portraitVector, RasterImage frame, BiometricModelOptions settings, CancellationToken token)
    {
        // The largest face of the frame (the detector gives the largest first): the validation already found one face in it.
        var faces = detector.Detect(frame, settings.DetectionScoreThreshold, token);
        if (faces.Count == 0)
            return 0;

        var vector = embedder.Embed(frame, faces[0], token);
        try
        {
            var similarity = Math.Clamp(EmbeddingMath.CosineSimilarity(portraitVector, vector), 0.0, 1.0);
            return double.IsFinite(similarity) ? similarity : 0;
        }
        finally
        {
            ImageBytes.Zero(vector);
        }
    }

    /// <summary>The report of the similarities of the chosen frames under the rules of the policy.</summary>
    private static MatchReport Report(List<double> similarities, IdentityMatchPolicy rules)
    {
        var aggregate = rules.Aggregation switch
        {
            IdentityAggregationRules.Median => Median(similarities),
            IdentityAggregationRules.Min => similarities.Min(),
            _ => similarities.Average(),
        };
        var min = similarities.Min();

        // Both conditions of an acceptance; the band is the aggregate alone, and apart.
        var acceptPassed = aggregate >= rules.Accept && min >= rules.MinEachFrame;
        var inReviewBand = aggregate >= rules.ReviewFloor && aggregate < rules.Accept;
        return new MatchReport(MatchState.Ok, aggregate, min, similarities.Count, acceptPassed, inReviewBand);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>The indices name frames of the sequence, each once, and there is at least one: comparing nothing is not a match.</summary>
    private static void RequireFrames(FrameSequence sequence, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
            throw new ArgumentException("At least one frame is needed to make the match.", nameof(indices));

        if (indices.Any(index => index < 0 || index >= sequence.Frames.Count))
            throw new ArgumentException("An index is not a frame of the sequence.", nameof(indices));

        if (indices.Distinct().Count() != indices.Count)
            throw new ArgumentException("A frame is named twice.", nameof(indices));
    }
}
