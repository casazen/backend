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

public sealed class SyntheticSelfieRegressionTests
{
    private const string Token = "token";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task SyntheticSelfieRegression_LegacyTwoFrameRoute_Answers409PresenceSequenceRequired_AndNothingIsRead()
    {
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
        // Defence in depth: the flow itself, not only the routes, runs no two-frame check.
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

        Assert.True(SyntheticIdentityFixtures.MeanDifferenceFromTheFirst(sent) < world.Policy.Presence.Continuity.MinPixelDifference);
        var differentFrames = sent.Jpegs.Select(ScriptedModels.Key).Distinct(StringComparer.Ordinal).Count();
        if (variant == "repeated")
            Assert.Equal(1, differentFrames);
        else
            Assert.True(differentFrames > 1);

        // A review at once: no new recording can explain a still picture, so there is no guided retry to offer.
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

        // No compensation: the high scores buy nothing, and the models that judge a person are never asked about this sequence.
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.Liveness && reason.Result == VerificationReasons.Results.NotRun);
        Assert.Contains(reasons, reason => reason.Check == VerificationReasons.Checks.Similarity && reason.Result == VerificationReasons.Results.NotRun);
        Assert.Equal(0, world.Models.LivenessCalls);
        Assert.False(world.Models.PortraitWasEmbedded);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((IdentityOutcomeCodes.Review, BiometricOutcome.Escalate), (saved.IdentityOutcomeCode, saved.BiometricOutcome));
    }

    [Fact]
    public async Task SyntheticSelfieRegression_TheSameModels_ApproveAGenuineSequence_OnlyWhereTheSegmentAllowsIt()
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();

        var observed = await world.ReadyStayAsync();
        var (inObservation, _, _) = await world.AttemptAsync(observed, steps => SequenceTestFrames.Genuine(world.Policy, steps), liveness: 0.99, similarity: 0.99);
        Assert.Equal(IdentityVerificationGuestResults.Operator, inObservation.Result);
        Assert.Null(inObservation.LockCode);
        var observationRow = Assert.Single(await world.LogRowsAsync(observed));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Accept, IdentityDecisionMode.Observation), (observationRow.Outcome, observationRow.ShadowOutcome, observationRow.DecisionMode));
        Assert.Contains(VerificationReasons.Parse(observationRow.ReasonsJson), reason => reason.Check == VerificationReasons.Checks.ObservationMode);
        await world.AssertNeverApprovedAsync(observed, inObservation, shadowApprovalAllowed: true);

        world.Inner.Segment = EvidenceBundles.Allowed;
        var qualified = await world.ReadyStayAsync();
        var (approved, _, _) = await world.AttemptAsync(qualified, steps => SequenceTestFrames.Genuine(world.Policy, steps), liveness: 0.99, similarity: 0.99);
        Assert.Equal(IdentityVerificationGuestResults.Ok, approved.Result);
        Assert.Equal("4321", approved.LockCode);
        var approvalRow = Assert.Single(await world.LogRowsAsync(qualified));
        Assert.Equal((BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic), (approvalRow.Outcome, approvalRow.ShadowOutcome, approvalRow.DecisionMode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticSelfieRegression_NeutralAndTurnedPair_IsRefusedAsTooShort_AndNeverApproved(bool qualified)
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        world.Inner.Segment = qualified ? EvidenceBundles.Allowed : EvidenceBundles.Observation;
        var session = await world.ReadyStayAsync();

        var (first, _, _) = await world.AttemptAsync(session, _ => SyntheticIdentityFixtures.NeutralAndTurnedPair(world.Policy), liveness: 0.99, similarity: 0.99);

        Assert.Equal(IdentityVerificationGuestResults.Retry, first.Result);
        Assert.True(first.GuidedRetryAvailable);
        Assert.Contains(IdentityGuidanceCodes.PresenceTooShort, first.Guidance);
        Assert.Empty(await world.LogRowsAsync(session));
        await world.AssertNeverApprovedAsync(session, first);

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

// The pictures stay where they are: the repository is public and no picture of a person or of a document may be copied into it.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SyntheticReplayFactAttribute : FactAttribute
{
    public const string DirectoryVariable = "CASAZEN_SYNTHETIC_SELFIE_DIR";

    public const string SelfieFile = "selfie.jpg";
    public const string FrontFile = "front.jpg";
    public const string BackFile = "back.jpg";

    public SyntheticReplayFactAttribute()
    {
        if (SkipReason(Environment.GetEnvironmentVariable, File.Exists) is { } reason)
            Skip = reason;
    }

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
