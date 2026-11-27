using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Qualification;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F02/US01/T01 (NF-198, NF-192, decisions D6, D7, D12), the mandatory regression of the synthetic selfie. The previous proof (map §6) accepted a SYNTHETIC subject: one still
/// picture plus a turned one, <c>SELFIE 200 result: ok</c>. That must never happen again on the new path, so these tests send what the previous proof sent, and what a replay of it
/// would send, through the real flow, the real closure and the real pipeline (validation of the sequence, choice of the frames, liveness, match, the engine of the policy) over
/// doubles of the three models scripted to say the best: a liveness of 0.99 and a similarity of 0.99. Nothing is approved, in observation and in a segment that is qualified for
/// the automatic approval with the global flag on. A control proves the tests are not blind: the same doubles approve a genuine sequence there, and only there.
/// </summary>
public sealed class SyntheticSelfieRegressionTests
{
    private const string Token = "token";
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ───── a. the path that accepted the synthetic selfie is closed ─────

    [Fact]
    public async Task SyntheticSelfieRegression_LegacyTwoFrameRoute_Answers409PresenceSequenceRequired_AndNothingIsRead()
    {
        // The two routes of the previous proof (the pose challenge and the selfie with its turned selfie) answer 409 under the gate, before anything is read: no attempt is spent, no row is
        // written, no model is asked, and the session is as it was.
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var before = await world.ReadBackAsync(session.Id);
        var checkIn = new Mock<IGuestCheckInService>();
        checkIn.Setup(service => service.GetSessionByTokenAsync(Token)).ReturnsAsync(session);
        var controller = Controller(checkIn.Object, world.Flow(), world.Inner.Flags);
        var presence = await world.IssueAsync(session);

        var challenge = await controller.IssueSelfieChallenge(Token);
        var selfie = await controller.UploadSelfie(Token, Image("selfie"), turnedSelfie: Image("turnedSelfie"), challengeToken: "anything");
        var withTheProofOfThePresence = await controller.UploadSelfie(Token, Image("selfie"), turnedSelfie: Image("turnedSelfie"), challengeToken: presence.ChallengeToken);

        AssertPresenceSequenceRequired(challenge.Result);
        AssertPresenceSequenceRequired(selfie.Result);
        AssertPresenceSequenceRequired(withTheProofOfThePresence.Result);
        var after = await world.ReadBackAsync(session.Id);
        Assert.Equal((before.BiometricAttempts, before.BiometricOutcome, before.OperatorRequestedAt, before.IdentityClosedAt), (after.BiometricAttempts, after.BiometricOutcome, after.OperatorRequestedAt, after.IdentityClosedAt));
        Assert.Empty(await world.LogRowsAsync(session));
        Assert.Equal(0, world.Models.Detections + world.Models.Embeddings + world.Models.LivenessCalls);
        await world.AssertNeverApprovedAsync(session, answer: null);
    }

    [Fact]
    public async Task SyntheticSelfieRegression_TheFlowItself_RunsNoTwoFrameCheck_WithTheGateOn()
    {
        // Defence in depth: the flow called directly (not through the routes) issues no pose challenge and reads and compares nothing of two stills.
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var flow = world.Flow();

        Assert.Null(await flow.IssueSelfieChallengeAsync(session, Ct));
        using var neutral = new MemoryStream(TestJpeg.Noise(96, 72, 31));
        using var turned = new MemoryStream(TestJpeg.Noise(96, 72, 32));
        var answer = await flow.SubmitChallengedSelfieAsync(session, neutral, turned, "anything", null, null, Ct);

        Assert.Equal(SelfieStepStatus.ChallengeRequired, answer.Status);
        Assert.NotEqual(IdentityVerificationGuestResults.Ok, answer.Result);
        Assert.Empty(await world.LogRowsAsync(session));
        Assert.Equal(0, world.Models.Detections + world.Models.Embeddings + world.Models.LivenessCalls);
        await world.AssertNeverApprovedAsync(session, answer: null);
    }

    // ───── b. a still picture played again is never a presence ─────

    /// <summary>
    /// Proof (security, NF-192, NF-198): a sequence of the same still frame repeated, with and without the small jitter of a hand that holds it, is NEVER approved and never even
    /// shadow-approved, with a liveness of 0.99 and a similarity of 0.99 scripted for it; its continuity and its challenge fail, and the liveness and the comparison are never made. It holds in
    /// observation and in a segment qualified for the automatic approval (d): the decision is a review either way.
    /// </summary>
    [Theory]
    [InlineData("repeated", false)]
    [InlineData("repeated", true)]
    [InlineData("jitter", false)]
    [InlineData("jitter", true)]
    public async Task SyntheticSelfieRegression_StillFrameSequence_NeverApproved(string variant, bool qualified)
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        world.Inner.Segment = qualified ? EvidenceBundles.Allowed : EvidenceBundles.Observation;
        var session = await world.ReadyStayAsync();

        var (answer, sent, _) = await world.AttemptAsync(
            session,
            _ => variant == "repeated" ? SyntheticIdentityFixtures.StillFrameRepeated(world.Policy) : SyntheticIdentityFixtures.StillFrameWithJitter(world.Policy),
            liveness: 0.99,
            similarity: 0.99);

        // The premise: the pictures are a still picture for the policy (the same bytes, or a mean difference far under the least a moving head makes), and the jitter is there.
        Assert.True(SyntheticIdentityFixtures.MeanDifferenceFromTheFirst(sent) < world.Policy.Presence.Continuity.MinPixelDifference);
        var differentFrames = sent.Jpegs.Select(ScriptedModels.Key).Distinct(StringComparer.Ordinal).Count();
        if (variant == "repeated")
            Assert.Equal(1, differentFrames);
        else
            Assert.True(differentFrames > 1);

        // A review at once: the continuity can never be explained by a new recording, so there is no guided retry to offer.
        Assert.Equal(PresenceStepStatus.Done, answer.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        Assert.False(answer.GuidedRetryAvailable);
        await world.AssertNeverApprovedAsync(session, answer);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.Outcome, row.ShadowOutcome));
        Assert.Equal(qualified ? IdentityDecisionMode.Automatic : IdentityDecisionMode.Observation, row.DecisionMode);
        var reasons = VerificationReasons.Parse(row.ReasonsJson);
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.PresenceContinuity && reason.Result == VerificationReasons.Results.Failed);
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.PresenceChallenge && reason.Result == VerificationReasons.Results.Failed);

        // No compensation: the high scores of the models bought nothing, and the models that judge a person were never asked about this sequence.
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.Liveness && reason.Result == VerificationReasons.Results.NotRun);
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.Similarity && reason.Result == VerificationReasons.Results.NotRun);
        Assert.Equal(0, world.Models.LivenessCalls);
        Assert.False(world.Models.PortraitWasEmbedded);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((IdentityOutcomeCodes.Review, BiometricOutcome.Escalate), (saved.IdentityOutcomeCode, saved.BiometricOutcome));
    }

    /// <summary>The control: the very doubles, with a liveness of 0.99 and a similarity of 0.99, approve a genuine sequence in a qualified segment and put it in shadow approval in observation.</summary>
    [Fact]
    public async Task SyntheticSelfieRegression_TheSameModels_ApproveAGenuineSequence_OnlyWhereTheSegmentAllowsIt()
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();

        // Observation: the policy would approve (the shadow), a person decides.
        var observed = await world.ReadyStayAsync();
        var (inObservation, _, _) = await world.AttemptAsync(observed, steps => SequenceTestFrames.Genuine(world.Policy, steps), liveness: 0.99, similarity: 0.99);
        Assert.Equal(IdentityVerificationGuestResults.Operator, inObservation.Result);
        Assert.Null(inObservation.LockCode);
        var observationRow = Assert.Single(await world.LogRowsAsync(observed));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Accept, IdentityDecisionMode.Observation), (observationRow.Outcome, observationRow.ShadowOutcome, observationRow.DecisionMode));
        Assert.Contains(VerificationReasons.Parse(observationRow.ReasonsJson), reason => reason.Check == VerificationReasons.Checks.ObservationMode);
        await world.AssertNeverApprovedAsync(observed, inObservation, shadowApprovalAllowed: true);

        // A qualified segment with the global permission: the same evidence approves, through the success path of today.
        world.Inner.Segment = EvidenceBundles.Allowed;
        var qualified = await world.ReadyStayAsync();
        var (approved, _, _) = await world.AttemptAsync(qualified, steps => SequenceTestFrames.Genuine(world.Policy, steps), liveness: 0.99, similarity: 0.99);
        Assert.Equal(IdentityVerificationGuestResults.Ok, approved.Result);
        Assert.Equal("4321", approved.LockCode);
        var approvalRow = Assert.Single(await world.LogRowsAsync(qualified));
        Assert.Equal((BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic), (approvalRow.Outcome, approvalRow.ShadowOutcome, approvalRow.DecisionMode));
    }

    // ───── c. the two stills of the previous proof, as a sequence of two frames ─────

    /// <summary>
    /// Proof (security, NF-192, NF-198): the neutral picture and the turned one of the previous proof, sent as a sequence of two frames, are refused as too short and never approved: a
    /// guided retry, and then a person (observation and qualified segment alike), with a liveness of 0.99 and a similarity of 0.99 scripted for both frames.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticSelfieRegression_NeutralAndTurnedPair_IsRefusedAsTooShort_AndNeverApproved(bool qualified)
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        world.Inner.Segment = qualified ? EvidenceBundles.Allowed : EvidenceBundles.Observation;
        var session = await world.ReadyStayAsync();

        var (first, _, _) = await world.AttemptAsync(session, _ => SyntheticIdentityFixtures.NeutralAndTurnedPair(world.Policy), liveness: 0.99, similarity: 0.99);

        // Refused: too short. It is a guided retry (a new recording fixes it), not an approval, and it writes no row.
        Assert.Equal(IdentityVerificationGuestResults.Retry, first.Result);
        Assert.True(first.GuidedRetryAvailable);
        Assert.Contains(IdentityGuidanceCodes.PresenceTooShort, first.Guidance);
        Assert.Empty(await world.LogRowsAsync(session));
        await world.AssertNeverApprovedAsync(session, first);

        // The same two stills again: the guided retry is used, a person decides.
        var (second, _, _) = await world.AttemptAsync(session, _ => SyntheticIdentityFixtures.NeutralAndTurnedPair(world.Policy), liveness: 0.99, similarity: 0.99);

        Assert.Equal(IdentityVerificationGuestResults.Operator, second.Result);
        await world.AssertNeverApprovedAsync(session, second);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.Outcome, row.ShadowOutcome));
        Assert.Equal(qualified ? IdentityDecisionMode.Automatic : IdentityDecisionMode.Observation, row.DecisionMode);
        var reasons = VerificationReasons.Parse(row.ReasonsJson);
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.PresenceTiming && reason.Result == VerificationReasons.Results.Failed);
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.PresenceChallenge && reason.Result == VerificationReasons.Results.Failed);
        Assert.Equal(0, world.Models.LivenessCalls);
        Assert.False(world.Models.PortraitWasEmbedded);
    }

    // ───── the opt-in replay of the original files ─────

    [Fact]
    public void SyntheticReplayFact_SkipsUnlessTheVariablePointsToAFolderWithTheThreeFiles()
    {
        var present = new HashSet<string>(StringComparer.Ordinal) { "/pics/selfie.jpg", "/pics/front.jpg", "/pics/back.jpg" };
        bool Exists(string path) => present.Contains(path.Replace('\\', '/'));
        string? Read(string name, string? value) => name == SyntheticReplayFactAttribute.DirectoryVariable ? value : null;

        Assert.NotNull(SyntheticReplayFactAttribute.SkipReason(name => Read(name, null), Exists));
        Assert.NotNull(SyntheticReplayFactAttribute.SkipReason(name => Read(name, "  "), Exists));
        Assert.NotNull(SyntheticReplayFactAttribute.SkipReason(name => Read(name, "/other"), Exists));
        present.Remove("/pics/back.jpg");
        var missing = SyntheticReplayFactAttribute.SkipReason(name => Read(name, "/pics"), Exists);
        Assert.NotNull(missing);
        Assert.Contains("back.jpg", missing, StringComparison.Ordinal);
        Assert.Contains(SyntheticReplayFactAttribute.DirectoryVariable, missing, StringComparison.Ordinal);
        present.Add("/pics/back.jpg");
        Assert.Null(SyntheticReplayFactAttribute.SkipReason(name => Read(name, "/pics"), Exists));
    }

    /// <summary>
    /// The replay of the original files with the REAL models (opt-in: <c>CASAZEN_SYNTHETIC_SELFIE_DIR</c> points to a folder with <c>selfie.jpg</c>, <c>front.jpg</c> and <c>back.jpg</c>, for example
    /// <c>tmp/checkin-ocr-qa</c>; CI never runs it and the pictures are never copied into the repository). It builds from the still selfie the sequences a replay would send (the same bytes
    /// again, the picture held a little differently each time, the pair of two stills) and asks the real pipeline and the real policy of the application: a review in the strictest
    /// conditions (a segment that allows the approval, a document that is coherent), never an approval and never a shadow approval. Nothing of the pictures, no score, is printed.
    /// </summary>
    [SyntheticReplayFact]
    public async Task SyntheticSelfieReplay_OriginalFilesWithTheRealModels_NeverApproved()
    {
        var folder = Environment.GetEnvironmentVariable(SyntheticReplayFactAttribute.DirectoryVariable)!.Trim();
        var selfie = await File.ReadAllBytesAsync(Path.Combine(folder, SyntheticReplayFactAttribute.SelfieFile), Ct);
        var front = await File.ReadAllBytesAsync(Path.Combine(folder, SyntheticReplayFactAttribute.FrontFile), Ct);
        await using var pipeline = await QualificationPipeline.CreateAsync(new QualificationPipelineOptions { IncludeEnvironmentVariables = false }, Ct);
        var policy = pipeline.ActivePolicy;
        var collector = new PresenceEvidenceCollector(pipeline.Validator, pipeline.Selector, pipeline.Liveness, pipeline.Matcher, Options.Create(pipeline.BiometricOptions));

        foreach (var variant in new[] { "same bytes", "held differently", "two stills" })
        {
            using var sequence = ReplaySequence(variant, selfie, policy, pipeline.BiometricOptions.MaxImagePixels);
            var challenge = new StoredPresenceChallenge(Guid.NewGuid(), SequenceTestFrames.DefaultSteps, SequenceTestFrames.IssuedAt, SequenceTestFrames.IssuedAt.AddSeconds(policy.Presence.Challenge.LifetimeSeconds), 1);

            var evidence = await collector.CollectAsync(sequence, challenge, front, policy, Ct);

            var match = evidence.Match is { } made ? MatchSummary.From(made, policy.Match) : null;
            var bundle = new IdentityEvidenceBundle(
                IdentityPolicyState.Ready,
                null,
                DocumentComplete: true,
                IdentityDocumentCoherence.Passed,
                "it-cie-3.0",
                IdentityChannels.AndroidChrome,
                IdentityAssuranceLevels.Consistency,
                evidence.Checks(policy),
                match,
                evidence.ModelsFault,
                EvidenceBundles.Allowed,
                GuidedRetriesUsed: 0,
                GuidedRetriesAllowed: policy.Closure.GuidedRetries);
            var decision = pipeline.Engine.Decide(bundle);

            Assert.NotEqual(IdentityFinalDecision.AutoApproved, decision.Final);
            Assert.Equal(IdentityShadowDecision.Review, decision.Shadow);
            Assert.NotEqual(BiometricOutcome.Accept, decision.LogOutcome);
        }
    }

    // ───── helpers ─────

    /// <summary>A sequence the replay of the previous proof would send, built from the bytes of the still selfie: 51 frames at 8 a second, or the two stills a second and a half apart.</summary>
    private static FrameSequence ReplaySequence(string variant, byte[] selfie, IdentityDecisionPolicy policy, int maxPixels)
    {
        var count = variant == "two stills" ? 2 : 51;
        var interval = variant == "two stills" ? 1500L : (long)Math.Round(1000.0 / policy.Presence.Sequence.TargetFps);
        var frames = new List<SequenceFrame>(count);
        for (var index = 0; index < count; index++)
        {
            var picture = RasterImage.Decode(selfie, maxPixels);
            var bytes = (byte[])selfie.Clone();
            if (variant != "same bytes" && index > 0)
            {
                // The picture held a little differently each time: a crop of one to three pixels on each side, scaled back (a small shift and a small zoom).
                var margin = 1 + (index % 3);
                using var cropped = picture.Cropped(margin, margin, picture.Width - (2 * margin), picture.Height - (2 * margin));
                var held = cropped.Resized(picture.Width, picture.Height);
                picture.Dispose();
                picture = held;
                bytes = CardPngEncoder.Encode(held);
            }

            frames.Add(new SequenceFrame(index, index * interval, picture, bytes));
        }

        var duration = frames[^1].TimestampMs;
        return new FrameSequence(frames, SequenceTestFrames.IssuedAt.AddMilliseconds(duration + 1500));
    }

    private static void AssertPresenceSequenceRequired(IActionResult? result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(IdentityApiErrors.PresenceSequenceRequired, problem.Extensions["code"]);
    }

    private static FormFile Image(string field)
    {
        var bytes = TestJpeg.Noise(32, 24, 3);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, $"{field}.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg",
        };
    }

    private static PublicGuestCheckInController Controller(IGuestCheckInService checkIn, IIdentityVerificationFlowService flow, IFeatureFlags flags) => new(
        checkIn,
        Mock.Of<IAlloggiatiCodeTableService>(),
        Mock.Of<ICheckInDocumentReader>(),
        Mock.Of<IAlloggiatiReportScheduler>(),
        flow,
        flags,
        Mock.Of<IStringLocalizer<SharedResources>>(),
        NullLogger<PublicGuestCheckInController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        },
    };
}

/// <summary>
/// A fact of the replay of the original files of the previous proof (E74/F02/US01/T01): skipped, with its reason, unless <c>CASAZEN_SYNTHETIC_SELFIE_DIR</c> points to a folder that holds
/// <c>selfie.jpg</c>, <c>front.jpg</c> and <c>back.jpg</c>. The variable belongs to test runs only (not a setting of the application), CI sets none, and the pictures stay where they are:
/// the repository is public and no picture of a person or a document is ever copied into it.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SyntheticReplayFactAttribute : FactAttribute
{
    /// <summary>The folder of the original files of the previous proof.</summary>
    public const string DirectoryVariable = "CASAZEN_SYNTHETIC_SELFIE_DIR";

    public const string SelfieFile = "selfie.jpg";
    public const string FrontFile = "front.jpg";
    public const string BackFile = "back.jpg";

    public SyntheticReplayFactAttribute()
    {
        if (SkipReason(Environment.GetEnvironmentVariable, File.Exists) is { } reason)
            Skip = reason;
    }

    /// <summary>Why the replay must be skipped, or null when it can run. <paramref name="read"/> reads a variable; <paramref name="exists"/> says whether a file is there. Nothing is printed but names.</summary>
    public static string? SkipReason(Func<string, string?> read, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(exists);

        const string OptIn = "The replay of the original files of the previous proof is opt-in: it needs the files, which are never in the repository, so CI never runs it.";
        var folder = read(DirectoryVariable)?.Trim();
        if (string.IsNullOrEmpty(folder))
            return $"{OptIn} Set {DirectoryVariable} to a folder with {SelfieFile}, {FrontFile} and {BackFile} to run it.";

        var missing = new[] { SelfieFile, FrontFile, BackFile }.Where(file => !exists(Path.Combine(folder, file))).ToList();
        return missing.Count == 0 ? null : $"{OptIn} {DirectoryVariable} names a folder that lacks {string.Join(", ", missing)}.";
    }
}
