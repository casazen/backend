using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E08/F05/US02/T02 (NF-36, D7): what follows a calendar change after it was recorded (<see cref="CalendarChangeRunner"/>): the processes that are due, run oldest first and one org
/// at a time in a bounded batch; the chunks the channel manager accepted, confirmed only when its process says every date was processed (never when the endpoint is not there); and the
/// retry the host asks for. EF InMemory, the push and the channel manager mocked.
/// </summary>
public class CalendarChangeRunnerTests
{
    private static readonly Guid OrgA = Guid.Parse("3d5f7a91-2c4e-4b6d-8f1a-0c2e4a6b8d01");
    private static readonly Guid OrgB = Guid.Parse("3d5f7a91-2c4e-4b6d-8f1a-0c2e4a6b8d02");
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunDue_RunsTheProcessesThatAreDue_OldestFirst_OneOrgAtATime_AndLeavesTheOthersAlone()
    {
        var h = new Runner();
        var a1 = await h.SeedAsync(OrgA, CalendarChangeState.Queued, due: -30, CalendarChunkState.Pending);
        var b1 = await h.SeedAsync(OrgB, CalendarChangeState.Sending, due: -20, CalendarChunkState.Accepted, CalendarChunkState.Pending);
        var a2 = await h.SeedAsync(OrgA, CalendarChangeState.Queued, due: -10, CalendarChunkState.Pending);
        await h.SeedAsync(OrgA, CalendarChangeState.Queued, due: 5, CalendarChunkState.Pending);
        await h.SeedAsync(OrgA, CalendarChangeState.Failed, due: -10, CalendarChunkState.Failed);
        await h.SeedAsync(OrgA, CalendarChangeState.Queued, due: null, CalendarChunkState.Pending);
        var ran = new List<Guid>();
        h.Push.Setup(p => p.RunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Callback<Guid, CancellationToken>((id, _) => ran.Add(id)).Returns(Task.CompletedTask);

        var count = await h.Service().RunDueAsync();

        // The org of the oldest due process first (all its due processes), then the other org: never the future, a settled one or one with nothing waiting.
        Assert.Equal(3, count);
        Assert.Equal([a1, a2, b1], ran);
    }

    [Fact]
    public async Task RunDue_TakesAtMostTheBatchOfAnOrg_TheRestWaitsForTheNextRun_AndNothingRunsWhileTheConnectorIsOff()
    {
        var h = new Runner();
        var ids = new List<Guid>();
        for (var i = 0; i < CalendarChangeRunner.PerOrgBatch + 3; i++)
            ids.Add(await h.SeedAsync(OrgA, CalendarChangeState.Queued, due: -100 + i, CalendarChunkState.Pending));

        var ran = new List<Guid>();
        h.Push.Setup(p => p.RunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Callback<Guid, CancellationToken>((id, _) => ran.Add(id)).Returns(Task.CompletedTask);

        Assert.Equal(CalendarChangeRunner.PerOrgBatch, await h.Service().RunDueAsync());
        Assert.Equal(ids.Take(CalendarChangeRunner.PerOrgBatch), ran);

        var off = new Runner(connector: false);
        await off.SeedAsync(OrgA, CalendarChangeState.Queued, due: -10, CalendarChunkState.Pending);
        Assert.Equal(0, await off.Service().RunDueAsync());
        off.Push.Verify(p => p.RunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDue_AProcessThatFails_DoesNotStopTheOthers_AndTheFailureIsRaisedAtTheEnd()
    {
        var h = new Runner();
        var broken = await h.SeedAsync(OrgA, CalendarChangeState.Queued, due: -20, CalendarChunkState.Pending);
        var fine = await h.SeedAsync(OrgB, CalendarChangeState.Queued, due: -10, CalendarChunkState.Pending);
        var ran = new List<Guid>();
        h.Push.Setup(p => p.RunAsync(broken, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("a bug"));
        h.Push.Setup(p => p.RunAsync(fine, It.IsAny<CancellationToken>())).Callback<Guid, CancellationToken>((id, _) => ran.Add(id)).Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<AggregateException>(() => h.Service().RunDueAsync());

        Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions));
        Assert.Equal([fine], ran);
    }

    [Fact]
    public async Task Follow_ANewOrNotProcessedProcess_LeavesTheChunkAccepted_AndIsAskedAgainLater()
    {
        var h = new Runner();
        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1);
        h.Client
            .Setup(c => c.GetCalendarProcessAsync("100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("100", "NEW", [new OctorateCalendarProcessDate(null, "NOT_PROCESSED")]));

        await h.Service().RunDueAsync();

        var (process, chunks) = await h.ReadAsync(id);
        Assert.Equal(CalendarChunkState.Accepted, Assert.Single(chunks).State);
        Assert.Equal(CalendarChangeState.Sent, process.State);
        Assert.Null(process.FinishedAt);
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.ConfirmationEvery, process.NextAttemptAt);
    }

    [Fact]
    public async Task Follow_EveryDateProcessed_ConfirmsTheChunk_AndTheProcessIsConfirmedAndFinished()
    {
        var h = new Runner();
        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1);
        h.Client
            .Setup(c => c.GetCalendarProcessAsync("100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("100", Runner.Success, [new OctorateCalendarProcessDate(null, Runner.Success), new OctorateCalendarProcessDate(null, Runner.Success)]));

        await h.Service().RunDueAsync();

        var (process, chunks) = await h.ReadAsync(id);
        Assert.Equal(CalendarChunkState.Confirmed, Assert.Single(chunks).State);
        Assert.Equal(CalendarChangeState.Confirmed, process.State);
        Assert.Equal(Now.UtcDateTime, process.FinishedAt);
        // Asked once more later (below), never again after that.
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.ConfirmedRecheckAfter, process.NextAttemptAt);
        Assert.Null(process.ErrorCode);
    }

    /// <summary>
    /// PROOF TEST: data rule. A status that is unknown, interim (<c>PROCESSING</c>) or absent never confirms a chunk, and with no status of success configured (the default: none is
    /// documented) even a process that says it is done confirms nothing: the chunk stays «accepted by the channel manager».
    /// </summary>
    [Theory]
    [InlineData("PROCESSING", Runner.Success, Runner.Success)]
    [InlineData(Runner.Success, "SOMETHING_UNKNOWN", Runner.Success)]
    [InlineData(null, Runner.Success, Runner.Success)]
    [InlineData(Runner.Success, null, Runner.Success)]
    [InlineData(Runner.Success, Runner.Success, "")]
    public async Task Follow_AnUnknownInterimOrAbsentStatus_NeverConfirmsTheChunk_AndNoSuccessStatusConfiguredConfirmsNothing_Test(string? process, string? date, string configured)
    {
        var h = new Runner(successStatuses: configured);
        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1);
        h.Client
            .Setup(c => c.GetCalendarProcessAsync("100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("100", process, [new OctorateCalendarProcessDate(null, date)]));

        await h.Service().RunDueAsync();

        var (after, chunks) = await h.ReadAsync(id);
        Assert.Equal(CalendarChunkState.Accepted, Assert.Single(chunks).State);
        Assert.Equal(CalendarChangeState.Sent, after.State);
        Assert.Null(after.FinishedAt);
    }

    /// <summary>
    /// A process that is wholly confirmed is asked once more ten minutes later: an error the endpoint shows after the confirmation fails the chunk and is not left out of sight
    /// (and nothing is asked in between, nor again after the second look).
    /// </summary>
    [Fact]
    public async Task Follow_AnErrorThatShowsUpAfterTheConfirmation_FailsTheChunk_AndIsNotLeftOutOfSight()
    {
        var h = new Runner();
        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1);
        h.Client
            .SetupSequence(c => c.GetCalendarProcessAsync("100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("100", Runner.Success, [new OctorateCalendarProcessDate(null, Runner.Success)]))
            .ReturnsAsync(new OctorateCalendarProcess("100", Runner.Success, [new OctorateCalendarProcessDate(null, "ERROR")]));

        await h.Service().RunDueAsync();

        var (confirmed, confirmedChunks) = await h.ReadAsync(id);
        Assert.Equal((CalendarChangeState.Confirmed, CalendarChunkState.Confirmed), (confirmed.State, Assert.Single(confirmedChunks).State));
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.ConfirmedRecheckAfter, confirmed.NextAttemptAt);

        // Not due yet: nothing is asked.
        await h.Service().RunDueAsync();
        h.Client.Verify(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Due: the second look finds the error.
        await h.Service(Now.AddMinutes(11)).RunDueAsync();

        var (after, chunks) = await h.ReadAsync(id);
        var chunk = Assert.Single(chunks);
        Assert.Equal((CalendarChunkState.Failed, "octorate_channel_error"), (chunk.State, chunk.ErrorCode));
        Assert.Equal((CalendarChangeState.Failed, "octorate_channel_error"), (after.State, after.ErrorCode));
        Assert.Null(after.NextAttemptAt);
    }

    [Fact]
    public async Task Follow_AnErrorOfTheProcess_FailsTheChunkWithTheCodeOfTheChannelError()
    {
        var h = new Runner();
        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1);
        h.Client
            .Setup(c => c.GetCalendarProcessAsync("100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("100", Runner.Success, [new OctorateCalendarProcessDate(null, Runner.Success), new OctorateCalendarProcessDate(null, "ERROR")]));

        await h.Service().RunDueAsync();

        var (process, chunks) = await h.ReadAsync(id);
        var chunk = Assert.Single(chunks);
        Assert.Equal((CalendarChunkState.Failed, "octorate_channel_error"), (chunk.State, chunk.ErrorCode));
        Assert.Equal((CalendarChangeState.Failed, "octorate_channel_error"), (process.State, process.ErrorCode));
        Assert.Null(process.NextAttemptAt);
    }

    [Fact]
    public async Task Follow_AnEndpointThatIsNotThere_LeavesTheChunkAccepted_ClaimsNothing_AndIsNotAskedAgain()
    {
        var h = new Runner();
        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1);
        h.Client.Setup(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateNotFoundException());

        await h.Service().RunDueAsync();

        var (process, chunks) = await h.ReadAsync(id);
        Assert.Equal(CalendarChunkState.Accepted, Assert.Single(chunks).State);
        Assert.Equal(CalendarChangeState.Sent, process.State);
        Assert.Null(process.NextAttemptAt);
        Assert.Null(process.FinishedAt);
        h.Client.Verify(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Follow_AQuotaThatIsUsedUp_IsAskedAgainAfterFiveMinutes_NeverTakenForAnEndpointThatIsNotThere()
    {
        var h = new Runner();
        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1);
        // The 403 of a quota that is used up (OCT-13) is not a refusal of the endpoint: the chunk waits as the push waits for a quota.
        h.Client
            .Setup(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(403, quotaExhausted: true));

        await h.Service().RunDueAsync();

        var (process, chunks) = await h.ReadAsync(id);
        Assert.Equal(CalendarChunkState.Accepted, Assert.Single(chunks).State);
        Assert.Equal(CalendarChangeState.Sent, process.State);
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.QuotaRetryAfter, process.NextAttemptAt);
        h.Client.Verify(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The follow-up reads at most <see cref="CalendarChangeRunner.MaxReadsPerPass"/> processes of the channel manager for an org in one pass, all its change processes together (a bulk
    /// of fifty properties has hundreds of accepted chunks): the quota of the org is read once, the rest waits, accepted, and is asked again after the wait of a quota.
    /// </summary>
    [Fact]
    public async Task Follow_ReadsAtMostTheBudgetOfAPassForAnOrg_TheQuotaIsReadOnce_AndWhatIsLeftWaitsAccepted_Test()
    {
        var h = new Runner();
        var first = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: -10, Enumerable.Repeat(CalendarChunkState.Accepted, 20).ToArray());
        var second = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: -5, Enumerable.Repeat(CalendarChunkState.Accepted, 20).ToArray());

        await h.Service().RunDueAsync();

        // 20 reads for the first, the 5 that are left for the second.
        h.Client.Verify(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(CalendarChangeRunner.MaxReadsPerPass));
        h.Client.Verify(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>()), Times.Once);
        var (firstProcess, firstChunks) = await h.ReadAsync(first);
        var (secondProcess, secondChunks) = await h.ReadAsync(second);
        Assert.All(firstChunks.Concat(secondChunks), c => Assert.Equal(CalendarChunkState.Accepted, c.State));
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.ConfirmationEvery, firstProcess.NextAttemptAt);
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.QuotaRetryAfter, secondProcess.NextAttemptAt);
    }

    /// <summary>
    /// The follow-up stops at the quota of the channel manager less the reserve of the push (<c>QuotaReserve</c>, 20): with 164 and 140 used it reads 4, with 150 used none (the
    /// reserve is not its to take); a quota that cannot be read leaves nothing to read. What is not read stays accepted and is asked again after five minutes.
    /// </summary>
    [Theory]
    [InlineData(140, 4)]
    [InlineData(150, 0)]
    [InlineData(-1, 0)]
    public async Task Follow_StopsAtTheQuotaLessTheReserveOfThePush_Test(int used, int reads)
    {
        var h = new Runner();
        if (used >= 0)
            h.Client.Setup(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateConfiguration("SANDBOX", 164, used, null));
        else
            h.Client.Setup(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(503));

        var id = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, Enumerable.Repeat(CalendarChunkState.Accepted, 10).ToArray());

        await h.Service().RunDueAsync();

        h.Client.Verify(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(reads));
        var (process, chunks) = await h.ReadAsync(id);
        Assert.All(chunks, c => Assert.Equal(CalendarChunkState.Accepted, c.State));
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.QuotaRetryAfter, process.NextAttemptAt);
    }

    [Fact]
    public async Task Follow_ARefusalToAnswerNow_IsAskedAgainLater_ButOnlyWhileTheWindowOfTheConfirmationIsOpen()
    {
        var h = new Runner();
        var open = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 5);
        var over = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 31);
        h.Client.Setup(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(503));

        await h.Service().RunDueAsync();

        Assert.Equal(Now.UtcDateTime + CalendarPushRules.ConfirmationEvery, (await h.ReadAsync(open)).Process.NextAttemptAt);
        // Past 30 minutes the chunk stays «accepted by the channel manager» and is not asked again.
        Assert.Null((await h.ReadAsync(over)).Process.NextAttemptAt);
        Assert.Equal(CalendarChunkState.Accepted, (await h.ReadAsync(over)).Chunks.Single().State);
    }

    /// <summary>
    /// The window of the follow-up is the chunk's own: it starts at the last call of that chunk, not at the first call of the process. A chunk sent late (a quota that made a bulk
    /// wait, a retry the host asked for hours after the first call) is followed even though the process started two hours ago; one whose own window is over is not asked
    /// any more and stays accepted; and a process with nothing left to follow is not due again.
    /// </summary>
    [Fact]
    public async Task Follow_TheWindowIsTheChunksOwn_ALateChunkOfAnOldProcessIsFollowed_AndAnOldChunkIsNot_Test()
    {
        var h = new Runner();
        // The process started two hours ago; its first chunk was sent 45 minutes ago, its second one a minute ago.
        var late = await h.SeedAsync(
            OrgA,
            CalendarChangeState.Sent,
            due: 0,
            CalendarChunkState.Accepted,
            startedMinutesAgo: 120,
            more: [CalendarChunkState.Accepted],
            lastCallMinutesAgo: [45, 1]);

        await h.Service().RunDueAsync();

        // Only the chunk of the open window is asked (once), and the process is followed again in two minutes, for as long as that window is open.
        h.Client.Verify(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var (process, chunks) = await h.ReadAsync(late);
        Assert.All(chunks, c => Assert.Equal(CalendarChunkState.Accepted, c.State));
        Assert.Equal(Now.UtcDateTime + CalendarPushRules.ConfirmationEvery, process.NextAttemptAt);

        // A process whose chunks are all past their own window is not asked and is not due again.
        var old = await h.SeedAsync(OrgB, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 120, lastCallMinutesAgo: [45]);

        await h.Service().RunDueAsync();

        var (oldProcess, oldChunks) = await h.ReadAsync(old);
        Assert.Equal(CalendarChunkState.Accepted, Assert.Single(oldChunks).State);
        Assert.Null(oldProcess.NextAttemptAt);
        h.Client.Verify(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Follow_AChunkWhoseProcessIdsWereNotAllKept_IsNeverConfirmed_ButAnErrorOfAKeptOneFailsIt()
    {
        var h = new Runner();
        var kept = await h.SeedAsync(OrgA, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1, processIds: "100,101+");
        var failing = await h.SeedAsync(OrgB, CalendarChangeState.Sent, due: 0, CalendarChunkState.Accepted, startedMinutesAgo: 1, processIds: "200,201+");
        h.Client
            .Setup(c => c.GetCalendarProcessAsync(It.IsIn("100", "101"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("100", Runner.Success, [new OctorateCalendarProcessDate(null, Runner.Success)]));
        h.Client
            .Setup(c => c.GetCalendarProcessAsync("200", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("200", Runner.Success, [new OctorateCalendarProcessDate(null, "ERROR")]));
        h.Client
            .Setup(c => c.GetCalendarProcessAsync("201", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarProcess("201", Runner.Success, [new OctorateCalendarProcessDate(null, Runner.Success)]));

        await h.Service().RunDueAsync();

        Assert.Equal(CalendarChunkState.Accepted, (await h.ReadAsync(kept)).Chunks.Single().State);
        Assert.Equal(CalendarChunkState.Failed, (await h.ReadAsync(failing)).Chunks.Single().State);
    }

    [Fact]
    public async Task Retry_SendsAgainWhatFailed_ThePartlySentProcessKeepsItsAcceptedChunks_AndARunIsEnqueued()
    {
        var h = new Runner();
        var failed = await h.SeedAsync(OrgA, CalendarChangeState.Failed, due: null, CalendarChunkState.Failed);
        var partly = await h.SeedAsync(OrgA, CalendarChangeState.PartiallySent, due: null, CalendarChunkState.Accepted, CalendarChunkState.Failed);

        Assert.Equal(CalendarChangeState.Queued, await h.Service().RetryAsync(failed));
        Assert.Equal(CalendarChangeState.Sending, await h.Service().RetryAsync(partly));

        var (process, chunks) = await h.ReadAsync(failed);
        var chunk = Assert.Single(chunks);
        Assert.Equal((CalendarChunkState.Pending, 0, (string?)null), (chunk.State, chunk.Attempts, chunk.ErrorCode));
        Assert.Equal((CalendarChangeState.Queued, (string?)null, (DateTime?)null, (DateTime?)Now.UtcDateTime), (process.State, process.ErrorCode, process.FinishedAt, process.NextAttemptAt));
        var (partlyProcess, partlyChunks) = await h.ReadAsync(partly);
        Assert.Equal([CalendarChunkState.Pending, CalendarChunkState.Accepted], partlyChunks.Select(c => c.State).Order());
        Assert.Equal(CalendarChangeState.Sending, partlyProcess.State);
        h.Jobs.Verify(
            j => j.Create(
                It.Is<Job>(job => job.Type == typeof(CalendarChangeProcessJob) && job.Method.Name == nameof(CalendarChangeProcessJob.RunAsync)),
                It.IsAny<IState>()),
            Times.Exactly(2));
    }

    [Theory]
    [InlineData(CalendarChangeState.Applied)]
    [InlineData(CalendarChangeState.Queued)]
    [InlineData(CalendarChangeState.Sending)]
    [InlineData(CalendarChangeState.Sent)]
    [InlineData(CalendarChangeState.Confirmed)]
    public async Task Retry_OnlyAFailedOrPartlySentProcessCanBeSentAgain_AnyOtherIsAConflict_AndAnUnknownOneIsNotFound(CalendarChangeState state)
    {
        var h = new Runner();
        var id = await h.SeedAsync(OrgA, state, due: null, CalendarChunkState.Pending);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => h.Service().RetryAsync(id));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service().RetryAsync(Guid.NewGuid()));

        Assert.Equal("calendar_change_not_retryable", conflict.Code);
        Assert.Equal(CalendarChunkState.Pending, (await h.ReadAsync(id)).Chunks.Single().State);
        h.Jobs.VerifyNoOtherCalls();
    }

    private sealed class Runner
    {
        /// <summary>
        /// The status of success of these tests. No status of success is documented (the Postman examples are NEW, NOT_PROCESSED and ERROR): this one stands for whatever the sandbox
        /// shows and <c>Octorate:Calendar:ProcessSuccessStatuses</c> lists.
        /// </summary>
        public const string Success = "SUCCESS_UNDER_TEST";

        private readonly string _database = $"calendar-runner-{Guid.NewGuid():N}";
        private readonly bool _connector;
        private readonly string _successStatuses;

        public Runner(bool connector = true, string successStatuses = Success)
        {
            _connector = connector;
            _successStatuses = successStatuses;
            Db = NewDb();
            // Unless a test says otherwise the channel manager is still working on every process it is asked about.
            Client
                .Setup(c => c.GetCalendarProcessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OctorateCalendarProcess("100", "NEW", []));
        }

        public AppDbContext Db { get; }

        public Mock<ICalendarPushService> Push { get; } = new();

        public Mock<IOctorateApiClient> Client { get; } = new();

        public Mock<IBackgroundJobClient> Jobs { get; } = new();

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

        public CalendarChangeRunner Service(DateTimeOffset? at = null)
        {
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(_connector);
            return new CalendarChangeRunner(
                Db,
                Push.Object,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                flags.Object,
                Microsoft.Extensions.Options.Options.Create(new OctorateCalendarOptions { ProcessSuccessStatuses = _successStatuses }),
                new FixedTimeProvider(at ?? Now),
                NullLogger<CalendarChangeRunner>.Instance,
                Jobs.Object);
        }

        /// <summary>A process of <paramref name="orgId"/> with one chunk for each state given (the chunks that were accepted carry the process id «100» unless told otherwise), due <paramref name="due"/> minutes from now.</summary>
        public async Task<Guid> SeedAsync(
            Guid orgId,
            CalendarChangeState state,
            int? due,
            params CalendarChunkState[] chunkStates)
        {
            return await SeedAsync(orgId, state, due, chunkStates[0], startedMinutesAgo: 0, processIds: "100", more: chunkStates[1..]);
        }

        public async Task<Guid> SeedAsync(
            Guid orgId,
            CalendarChangeState state,
            int? due,
            CalendarChunkState first,
            int startedMinutesAgo,
            string processIds = "100",
            CalendarChunkState[]? more = null,
            int?[]? lastCallMinutesAgo = null)
        {
            var propertyId = Guid.NewGuid();
            var process = new CalendarChangeProcess
            {
                OrgId = orgId,
                Kind = CalendarChangeKind.Block,
                State = state,
                PropertyCount = 1,
                DayCount = 3,
                CreatedAt = Now.UtcDateTime.AddMinutes(-40),
                StartedAt = Now.UtcDateTime.AddMinutes(-startedMinutesAgo),
                NextAttemptAt = due is null ? null : Now.UtcDateTime.AddMinutes(due.Value),
                SummaryJson = CalendarChangeSummary.Build([new CalendarWindow(propertyId, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12))], null),
            };
            await using var db = NewDb();
            db.CalendarChangeProcesses.Add(process);
            var all = new[] { first }.Concat(more ?? []).ToList();
            for (var i = 0; i < all.Count; i++)
            {
                db.CalendarChangeChunks.Add(new CalendarChangeChunk
                {
                    OrgId = orgId,
                    ProcessId = process.Id,
                    PropertyId = propertyId,
                    FromDate = new DateOnly(2026, 10, 10).AddDays(i * 3),
                    ToDate = new DateOnly(2026, 10, 12).AddDays(i * 3),
                    State = all[i],
                    OctorateProcessId = all[i] is CalendarChunkState.Accepted or CalendarChunkState.Confirmed ? processIds : null,
                    ErrorCode = all[i] == CalendarChunkState.Failed ? "octorate_unavailable" : null,
                    Attempts = all[i] == CalendarChunkState.Failed ? 5 : 1,
                    LastAttemptAt = lastCallMinutesAgo is { } calls && i < calls.Length && calls[i] is { } ago ? Now.UtcDateTime.AddMinutes(-ago) : null,
                });
            }

            await db.SaveChangesAsync();
            return process.Id;
        }

        public async Task<(CalendarChangeProcess Process, List<CalendarChangeChunk> Chunks)> ReadAsync(Guid id)
        {
            await using var db = NewDb();
            return (
                await db.CalendarChangeProcesses.AsNoTracking().SingleAsync(p => p.Id == id),
                await db.CalendarChangeChunks.AsNoTracking().Where(c => c.ProcessId == id).OrderBy(c => c.FromDate).ToListAsync());
        }
    }
}
