using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    public async Task PresenceClosureIdempotency_ATwinThatWritesTheRowFirst_IsAnsweredWithTheStoredOutcome()
    {
        // The last line (AC3, g): a twin of the attempt (another request, another process) writes the row of this key after this request read the replay again under the lock and before its
        // save. The unique index refuses this save (emulated here, as PostgreSQL does it in PresenceClosurePostgresTests), the one save of this request is lost whole, and the answer is the
        // outcome the twin stored, as a replay: no error, one row, nothing of the session moved.
        var unique = new UniqueIdempotencyKeyInterceptor();
        await using var world = await PresenceClosureWorld.CreateAsync(true, unique);
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        var key = PresenceClosureKit.NewKey();
        var twinRow = TwinRow(session, key);
        world.Collector.OnCollect = async () =>
        {
            await using var twin = world.NewContext();
            twin.BiometricVerificationLogs.Add(twinRow);
            await twin.SaveChangesAsync(Ct);
        };

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, token), Ct);

        Assert.Equal((PresenceStepStatus.Done, IdentityVerificationGuestResults.Operator, true), (answer.Status, answer.Result, answer.Replayed));
        Assert.NotNull(answer.State);
        Assert.Equal(1, unique.Refused);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal(twinRow.Id, row.Id);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((0, (DateTime?)null, (string?)null), (saved.BiometricAttempts, saved.IdentityClosedAt, saved.IdentityLastAttemptKey));
        Assert.NotNull(PresenceClosureKit.ReadCheck(saved).Presence);
        Assert.Empty(world.Alerts.Raised);
        Assert.Equal(1, world.Collector.Calls);
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
    public async Task PresenceClosureIdempotency_TheDecisionAloneIsInTheQueue_AndAReplayTellsTheOperatorsOnce()
    {
        // Should the process stop between the save and the hand-off, the one save already holds what the queue of the operators reads: the session is not accepted and its outcome is the
        // review (VerificationOperatorRules.NeedsOperator). The next request with the same key finishes the hand-off, and only once: nobody tells the operators again after it.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var key = PresenceClosureKit.NewKey();
        await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, await world.IssueChallengeAsync(session)), Ct);
        session.OperatorRequestedAt = null;
        await world.Db.SaveChangesAsync(Ct);
        world.Alerts.Raised.Clear();
        Assert.True(VerificationOperatorRules.NeedsOperator.Compile()(await world.ReadBackAsync(session.Id)));

        var replay = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, null), Ct);

        Assert.Equal((IdentityVerificationGuestResults.Operator, true), (replay.Result, replay.Replayed));
        Assert.Equal([(session.Id, VerificationAlertReasons.Review)], world.Alerts.Raised);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.True(VerificationOperatorRules.NeedsOperator.Compile()(saved));

        await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, null), Ct);
        Assert.Single(world.Alerts.Raised);
        Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal(1, world.Collector.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceClosureIdempotency_AClientThatDropsAfterTheSave_StillGetsTheEffectsOfTheDecision(bool approval)
    {
        // The decision is committed, so its effects are not the request's any more: the connection drops the moment the one save is done (the token of the request is cancelled) and the operators
        // are told (or the lock code released), the encrypted picture of the document is deleted, with no token of the request.
        var drop = new FailingSaveInterceptor();
        await using var world = await PresenceClosureWorld.CreateAsync(true, drop);
        world.Segment = approval ? EvidenceBundles.Allowed : EvidenceBundles.Observation;
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        using var dropped = new CancellationTokenSource();
        drop.AfterSave = dropped.Cancel;

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), dropped.Token);
        drop.AfterSave = null;

        Assert.True(dropped.IsCancellationRequested);
        Assert.Equal(approval ? IdentityVerificationGuestResults.Ok : IdentityVerificationGuestResults.Operator, answer.Result);
        Assert.Empty(world.Storage.Objects);
        Assert.Equal(approval ? "4321" : null, answer.LockCode);
        var saved = await world.ReadBackAsync(session.Id);
        if (approval)
        {
            Assert.Equal([(session.OrgId, session.BookingId)], world.Alerts.Resolved);
            Assert.Null(saved.OperatorRequestedAt);
        }
        else
        {
            Assert.Equal([(session.Id, VerificationAlertReasons.Review)], world.Alerts.Raised);
            Assert.NotNull(saved.OperatorRequestedAt);
            Assert.All(world.Alerts.RaisedWith, raisedWith => Assert.False(raisedWith.CanBeCanceled));
        }
    }

    [Fact]
    public async Task PresenceClosureIdempotency_AFailureOfTheHandOff_IsFinishedByTheNextRequestWithTheSameKey()
    {
        // The decision is committed (the row, the outcome, the place in the queue) and the hand-off throws: the operators are not told. The page asks again with the same key, on a session
        // loaded again: the replay tells them, once, and decides nothing again.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        var key = PresenceClosureKit.NewKey();
        world.Alerts.FailNextRaise = new InvalidOperationException("the push provider is down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, token), Ct));

        var committed = await world.ReadBackAsync(session.Id);
        Assert.Equal((BiometricOutcome.Escalate, (DateTime?)null), (committed.BiometricOutcome, committed.OperatorRequestedAt));
        Assert.NotNull(committed.IdentityClosedAt);
        Assert.Single(await world.LogRowsAsync(session));
        Assert.Empty(world.Alerts.Raised);

        await using var other = world.NewContext();
        var reloaded = await world.LoadAsync(session.Id, other);
        var again = await world.Flow(other).SubmitPresenceSequenceAsync(reloaded, PresenceClosureKit.Submission(key, null), Ct);

        Assert.Equal((IdentityVerificationGuestResults.Operator, true), (again.Result, again.Replayed));
        Assert.Equal([(session.Id, VerificationAlertReasons.Review)], world.Alerts.Raised);
        Assert.NotNull((await world.ReadBackAsync(session.Id)).OperatorRequestedAt);
        Assert.Empty(world.Storage.Objects);

        await world.Flow(other).SubmitPresenceSequenceAsync(reloaded, PresenceClosureKit.Submission(key, null), Ct);
        Assert.Single(world.Alerts.Raised);
        Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal(1, world.Collector.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceClosureIdempotency_AFailureOfTheDeletion_IsFinishedByTheNextRequestWithTheSameKey(bool approval)
    {
        // The storage fails when the kept picture of the document is deleted. A review tells the operators all the same, then throws; an approval throws before the code is released. The page
        // asks again with the same key: the replay deletes the picture (and the rows are marked), the operators are not told twice, the code is released.
        await using var world = await PresenceClosureWorld.CreateAsync();
        world.Segment = approval ? EvidenceBundles.Allowed : EvidenceBundles.Observation;
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        var key = PresenceClosureKit.NewKey();
        world.Storage.FailNextDelete = new InvalidOperationException("the storage is down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, token), Ct));

        Assert.NotEmpty(world.Storage.Objects);
        Assert.Equal(approval ? BiometricOutcome.Accept : BiometricOutcome.Escalate, Assert.Single(await world.LogRowsAsync(session)).Outcome);
        Assert.Equal(approval ? 0 : 1, world.Alerts.Raised.Count);
        world.AccessCodes.Verify(codes => codes.ReleaseToGuestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await using var other = world.NewContext();
        var reloaded = await world.LoadAsync(session.Id, other);
        var again = await world.Flow(other).SubmitPresenceSequenceAsync(reloaded, PresenceClosureKit.Submission(key, null), Ct);

        Assert.True(again.Replayed);
        Assert.Equal(approval ? IdentityVerificationGuestResults.Ok : IdentityVerificationGuestResults.Operator, again.Result);
        Assert.Equal(approval ? "4321" : null, again.LockCode);
        Assert.Empty(world.Storage.Objects);
        Assert.Equal(approval ? 0 : 1, world.Alerts.Raised.Count);
        await using var read = world.NewContext();
        Assert.All(await read.DocumentImages.IgnoreQueryFilters().AsNoTracking().ToListAsync(Ct), image => Assert.NotNull(image.DeletedAt));
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

    /// <summary>The row of a twin that closed the same attempt: a review in observation with the key, as the closure writes it.</summary>
    private static BiometricVerificationLog TwinRow(GuestCheckInSession session, string key) => new()
    {
        OrgId = session.OrgId,
        BookingId = session.BookingId,
        GuestId = session.Booking.GuestId,
        Outcome = BiometricOutcome.Escalate,
        Attempts = 1,
        ModelName = "SFace",
        ModelVersion = "2021dec",
        PolicyVersion = "identity-policy-2026.11-1",
        PolicyHash = new string('c', 64),
        DecisionMode = IdentityDecisionMode.Observation,
        ShadowOutcome = BiometricOutcome.Escalate,
        FormatId = "it-cie-3.0",
        Channel = IdentityChannels.AndroidChrome,
        IdempotencyKey = key,
        CreatedAt = PresenceClosureKit.Now.UtcDateTime,
    };

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
