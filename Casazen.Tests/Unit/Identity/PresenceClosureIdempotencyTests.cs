using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US01/T02 (NF-194, AC-NF194-1, AC-NF192-5, decision D8): the explicit key of an attempt (<c>Idempotency-Key</c>). The same key gets the same answer and never a second
/// outcome: after a review, after a guided retry, after the challenge expired, and for two copies of the request at the same time. A key is the key of its booking only.
/// </summary>
public sealed class PresenceClosureIdempotencyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task PresenceClosureIdempotency_SameKey_SameAnswerOneRow()
    {
        // Proof (data, AC2): the network repeats the request; the second one is answered as the first, from the outcome kept, without reading a frame, without a second row, a second
        // alert or a second attempt, also once the challenge it carries has expired and with frames that are not even a sequence.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        var key = PresenceClosureKit.NewKey();

        var first = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, token), Ct);
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        var again = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, token, seed: 99) with { TimestampsJson = "not json" }, Ct);

        Assert.Equal(PresenceStepStatus.Done, again.Status);
        Assert.True(again.Replayed);
        Assert.Equal((first.Result, first.GuidedRetryAvailable, first.LockCode), (again.Result, again.GuidedRetryAvailable, again.LockCode));
        Assert.Equal(first.Guidance, again.Guidance);
        Assert.Equal(Json(first.State), Json(again.State));
        Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal(1, world.Collector.Calls);
        Assert.Single(world.Alerts.Raised);
        Assert.Equal(1, (await world.ReadBackAsync(session.Id)).BiometricAttempts);

        // The same key on a fresh copy of the session (another request of the page, loaded again) is answered from the log row too.
        await using var other = world.NewContext();
        var reloaded = await world.LoadAsync(session.Id, other);
        var third = await world.Flow(other).SubmitPresenceSequenceAsync(reloaded, PresenceClosureKit.Submission(key, null), Ct);
        Assert.Equal((first.Result, true), (third.Result, third.Replayed));
        Assert.Single(await world.LogRowsAsync(session));
    }

    [Fact]
    public async Task PresenceClosureIdempotency_AGuidedRetry_IsAnsweredAgain_WithoutASecondRetry()
    {
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        world.Collector.Next(PresenceClosureKit.CorrectableFailure(IdentityGuidanceCodes.PresenceChallengeNotFollowed));
        var key = PresenceClosureKit.NewKey();

        var first = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, await world.IssueChallengeAsync(session)), Ct);
        var again = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, null), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Retry, first.Result);
        Assert.Equal((first.Result, first.GuidedRetryAvailable, true), (again.Result, again.GuidedRetryAvailable, again.Replayed));
        Assert.Equal([IdentityGuidanceCodes.PresenceChallengeNotFollowed], again.Guidance);
        Assert.Equal(Json(first.State), Json(again.State));
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((1, 0), (saved.IdentityGuidedRetries, saved.BiometricAttempts));
        Assert.Empty(await world.LogRowsAsync(session));
        Assert.Equal(1, world.Collector.Calls);
    }

    [Fact]
    public async Task PresenceClosureIdempotency_ConcurrentDuplicates_DecideOnce()
    {
        // AC3 in memory (the row lock is modelled by the semaphores of the flow; the PostgreSQL twin is PresenceClosurePostgresTests): two copies of the request at the same moment,
        // each on its own context as two requests would be, decide once and answer the same.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        var key = PresenceClosureKit.NewKey();
        await using var leftDb = world.NewContext();
        await using var rightDb = world.NewContext();
        var left = await world.LoadAsync(session.Id, leftDb);
        var right = await world.LoadAsync(session.Id, rightDb);

        var answers = await Task.WhenAll(
            Task.Run(() => world.Flow(leftDb).SubmitPresenceSequenceAsync(left, PresenceClosureKit.Submission(key, token), Ct)),
            Task.Run(() => world.Flow(rightDb).SubmitPresenceSequenceAsync(right, PresenceClosureKit.Submission(key, token), Ct)));

        Assert.All(answers, answer => Assert.Equal((PresenceStepStatus.Done, IdentityVerificationGuestResults.Operator), (answer.Status, answer.Result)));
        Assert.Single(answers, answer => answer.Replayed);
        Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal(1, world.Collector.Calls);
        Assert.Single(world.Alerts.Raised);
        Assert.Equal(1, (await world.ReadBackAsync(session.Id)).BiometricAttempts);
    }

    [Fact]
    public async Task PresenceClosureIdempotency_ANewKeyAfterTheClosure_IsRefused_AsAlreadyClosed()
    {
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);

        var another = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);

        Assert.Equal(PresenceStepStatus.AlreadyClosed, another.Status);
        Assert.True(another.State!.Closed);
        Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal(1, world.Collector.Calls);
    }

    [Fact]
    public async Task PresenceClosureIdempotency_TheDecisionAlone_PutsTheSessionInTheQueueOfTheOperators()
    {
        // A replay runs no effect (the effects are the request's that decided). Should the process stop between the save and the hand-off, the one save already holds what the queue
        // of the operators reads: the session is not accepted and its outcome is the review (VerificationOperatorRules.NeedsOperator).
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var key = PresenceClosureKit.NewKey();
        await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, await world.IssueChallengeAsync(session)), Ct);
        session.OperatorRequestedAt = null;
        await world.Db.SaveChangesAsync(Ct);
        world.Alerts.Raised.Clear();

        var replay = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, null), Ct);

        Assert.Equal((IdentityVerificationGuestResults.Operator, true), (replay.Result, replay.Replayed));
        Assert.Empty(world.Alerts.Raised);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.True(VerificationOperatorRules.NeedsOperator.Compile()(saved));
        Assert.Single(await world.LogRowsAsync(session));
    }

    [Fact]
    public async Task PresenceClosureIdempotency_TheKeyOfAnotherBooking_IsNotAReplayHere()
    {
        // Isolation: a key is the key of its booking (the unique index is on booking and key). The same key used by another stay is a new attempt here, decided on its own.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var first = await world.ReadyStayAsync();
        var second = await world.ReadyStayAsync();
        var key = PresenceClosureKit.NewKey();
        world.Segment = EvidenceBundles.Allowed;
        await world.Flow().SubmitPresenceSequenceAsync(first, PresenceClosureKit.Submission(key, await world.IssueChallengeAsync(first)), Ct);
        world.Segment = EvidenceBundles.Observation;

        var answer = await world.Flow().SubmitPresenceSequenceAsync(second, PresenceClosureKit.Submission(key, await world.IssueChallengeAsync(second)), Ct);

        Assert.False(answer.Replayed);
        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        Assert.Equal(BiometricOutcome.Escalate, Assert.Single(await world.LogRowsAsync(second)).Outcome);
        Assert.Equal(BiometricOutcome.Accept, Assert.Single(await world.LogRowsAsync(first)).Outcome);
        Assert.Equal(2, world.Collector.Calls);
    }

    [Fact]
    public async Task PresenceClosureIdempotency_ThePageGetsTheSameBody_ForTheSameKey()
    {
        // The cross-repository contract: the body of the answer is the same JSON for the request and for its replay, through the real endpoint.
        await using var world = await PresenceClosureWorld.CreateAsync();
        world.Segment = EvidenceBundles.Allowed;
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        var key = PresenceClosureKit.NewKey();
        var controller = Controller(world, session);

        var first = await controller.SubmitPresence("token", world.Flow(), Frames(), "[0,125,250,375]", token, idempotencyKey: key);
        var again = await controller.SubmitPresence("token", world.Flow(), Frames(seed: 7), "[0,125,250,375]", token, idempotencyKey: key);

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var firstBody = JsonSerializer.Serialize(Assert.IsType<PresenceStepResponseDto>(Assert.IsType<OkObjectResult>(first.Result).Value), web);
        var againBody = JsonSerializer.Serialize(Assert.IsType<PresenceStepResponseDto>(Assert.IsType<OkObjectResult>(again.Result).Value), web);
        Assert.Equal(firstBody, againBody);
        using var json = JsonDocument.Parse(firstBody);
        Assert.Equal("ok", json.RootElement.GetProperty("result").GetString());
        Assert.Equal("4321", json.RootElement.GetProperty("lockCode").GetString());
        Assert.Single(await world.LogRowsAsync(session));
    }

    /// <summary>The state as the page reads it: a record that holds lists compares them by reference, the JSON by their content.</summary>
    private static string Json(IdentityVerificationState? state) =>
        JsonSerializer.Serialize(state is null ? null : PublicIdentityVerificationStateDto.From(state), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static List<IFormFile> Frames(int count = 4, int seed = 1)
    {
        var frames = new List<IFormFile>(count);
        for (var index = 0; index < count; index++)
        {
            var bytes = TestJpeg.Noise(32, 24, seed + index);
            frames.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, "frames", $"frame-{index}.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg",
            });
        }

        return frames;
    }

    private static PublicGuestCheckInController Controller(PresenceClosureWorld world, GuestCheckInSession session)
    {
        var checkIn = new Mock<IGuestCheckInService>();
        checkIn.Setup(service => service.GetSessionByTokenAsync("token")).ReturnsAsync(session);
        return new PublicGuestCheckInController(
            checkIn.Object,
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<ICheckInDocumentReader>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            world.Flow(),
            world.Flags,
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
}
