using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E08/F05/US02/T01 (NF-36, NF-78): the recording of a calendar change (<see cref="CalendarChangeRecorder"/>) and its push to the channel manager
/// (<see cref="CalendarPushService"/>), on EF InMemory with the client of Octorate mocked. The three proof tests are the data rules of the brief: an answer without a process
/// id is never an acceptance, and a property whose mapping is not confirmed receives nothing. The others pin the behaviour around them.
/// </summary>
public class CalendarPushProofTests
{
    // --- The proofs --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (no false success). The channel manager answers 200 with no process id (a room with no license writes nothing): the chunk is <c>Failed</c> with
    /// <c>octorate_room_not_licensed</c>, it keeps no process id, and the process is not <c>Sent</c> (nor confirmed): it is <c>Failed</c> and nothing waits.
    /// </summary>
    [Fact]
    public async Task CalendarPush_AnswerWithoutProcessId_IsFailedNotAccepted_Test()
    {
        var h = new Push();
        var (process, _) = await h.SeedAsync();
        h.Client
            .Setup(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarUpdateResult([]));

        await h.Service().RunAsync(process.Id);

        var chunk = await h.ChunkAsync();
        var after = await h.ProcessAsync();
        Assert.Equal(CalendarChunkState.Failed, chunk.State);
        Assert.Equal("octorate_room_not_licensed", chunk.ErrorCode);
        Assert.Null(chunk.OctorateProcessId);
        Assert.Equal(CalendarChangeState.Failed, after.State);
        Assert.NotEqual(CalendarChangeState.Sent, after.State);
        Assert.NotEqual(CalendarChangeState.Confirmed, after.State);
        Assert.Equal("octorate_room_not_licensed", after.ErrorCode);
        Assert.Null(after.NextAttemptAt);
        Assert.NotNull(after.FinishedAt);
        h.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// PROOF TEST: data rule. A property whose mapping row is proposed, or ambiguous, gets no chunk when its change is recorded and no call to Octorate leaves (not the write, not
    /// the read of its rooms, not the quota); a chunk that was recorded while the mapping was confirmed and finds it proposed when it is sent is <c>Skipped</c>, never sent.
    /// </summary>
    [Fact]
    public async Task CalendarPush_UnconfirmedMapping_SendsNothing_Test()
    {
        var h = new Push();
        var second = h.AddProperty("Casa Ambigua");
        var connection = new OctorateConnection { OrgId = Push.OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        h.Db.OctorateConnections.Add(connection);
        h.Db.OctorateListingMappings.AddRange(
            Mapping(connection.Id, "ACC-PROPOSED", OctorateMappingStatus.Proposed, h.PropertyId, productId: "956528"),
            Mapping(connection.Id, "ACC-AMBIGUOUS", OctorateMappingStatus.Ambiguous, null, productId: null, candidates: $"[\"{h.PropertyId}\",\"{second}\"]"));
        await h.Db.SaveChangesAsync();

        // The mapping of both is not confirmed: the channel manager must not be written to (OCT-04), so both are write-blocked.
        var mappings = new Mock<IChannelMappingService>();
        mappings.Setup(m => m.IsWriteBlockedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var targets = h.RealTargets(mappings.Object);
        var recorder = h.Recorder(targets);
        var window = new CalendarWindow(h.PropertyId, Push.Today.AddDays(1), Push.Today.AddDays(10));

        var record = await recorder.RecordAsync(
            CalendarChangeKind.Block, [window, new CalendarWindow(second, Push.Today.AddDays(1), Push.Today.AddDays(3))], null, null);
        await h.Db.SaveChangesAsync();

        Assert.Empty(record.Chunks);
        Assert.Equal(CalendarChangeState.Applied, record.Process.State);
        Assert.False(record.NeedsRun);
        Assert.Equal(0, await h.Db.CalendarChangeChunks.CountAsync());
        Assert.Equal(CalendarChangeState.Applied, (await h.ProcessAsync()).State);

        // A chunk that exists from the time the mapping was confirmed finds it proposed now: skipped, not sent.
        var (process, _) = await h.SeedAsync();
        await h.Service(targets).RunAsync(process.Id);

        Assert.Equal(CalendarChunkState.Skipped, (await h.Db.CalendarChangeChunks.AsNoTracking().SingleAsync()).State);
        Assert.Equal(CalendarChangeState.Applied, (await h.Db.CalendarChangeProcesses.AsNoTracking().SingleAsync(p => p.Id == process.Id)).State);
        h.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.GetRoomsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- The recorder ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Record_AConnectedProperty_GetsChunksOfAtMostTheConfiguredDays_AndAQueuedProcessWithItsQuotaEstimate()
    {
        var h = new Push();
        var window = new CalendarWindow(h.PropertyId, Push.Today.AddDays(1), Push.Today.AddDays(100));

        var record = await h.Recorder().RecordAsync(CalendarChangeKind.Bulk, [window], "auth0|host", """{"price":{"action":"Set","value":120}}""");

        // 100 days in calls of 62: two chunks (62 and 38 days), 5 and 3 units of quota.
        Assert.Equal([(62, CalendarChunkState.Pending), (38, CalendarChunkState.Pending)], record.Chunks.Select(c => (c.ToDate.DayNumber - c.FromDate.DayNumber + 1, c.State)));
        Assert.Equal(window.From, record.Chunks[0].FromDate);
        Assert.Equal(window.To, record.Chunks[1].ToDate);
        Assert.Equal(record.Chunks[0].ToDate.AddDays(1), record.Chunks[1].FromDate);
        var process = record.Process;
        Assert.Equal((CalendarChangeKind.Bulk, CalendarChangeState.Queued, 1, 100, 8), (process.Kind, process.State, process.PropertyCount, process.DayCount, process.QuotaUnitsEstimated));
        Assert.Equal("auth0|host", process.RequestedByUserId);
        Assert.True(record.NeedsRun);
        Assert.Equal(Push.Now.UtcDateTime, process.NextAttemptAt);
        Assert.All(record.Chunks, c => Assert.Equal((Push.OrgId, process.Id, h.PropertyId), (c.OrgId, c.ProcessId, c.PropertyId)));

        // The summary names the property and the dates, and what the caller said: never a name.
        Assert.Equal([h.PropertyId], CalendarChangeSummary.PropertyIdsOf(process.SummaryJson));
        Assert.Equal((window.From, window.To), CalendarChangeSummary.DatesOf(process.SummaryJson));
        Assert.Contains("\"action\":\"Set\"", process.SummaryJson);

        // Recorded in the unit of work, not saved.
        Assert.Equal(0, await h.NewDb().CalendarChangeProcesses.CountAsync());
        Assert.Equal(EntityState.Added, h.Db.Entry(process).State);
        await h.Db.SaveChangesAsync();
        Assert.Equal(2, await h.NewDb().CalendarChangeChunks.CountAsync());
    }

    [Fact]
    public async Task Record_TheDaysBeforeTodayAreNeverSent_AndAWindowThatIsOverRecordsAnAppliedProcess()
    {
        var h = new Push();

        var cut = await h.Recorder().RecordAsync(
            CalendarChangeKind.DayRules, [new CalendarWindow(h.PropertyId, Push.Today.AddDays(-5), Push.Today.AddDays(2))], null, null);
        var over = await h.Recorder().RecordAsync(
            CalendarChangeKind.DayRules, [new CalendarWindow(h.PropertyId, Push.Today.AddDays(-5), Push.Today.AddDays(-1))], null, null);

        var chunk = Assert.Single(cut.Chunks);
        Assert.Equal((Push.Today, Push.Today.AddDays(2)), (chunk.FromDate, chunk.ToDate));
        Assert.Equal(3, cut.Process.DayCount);
        Assert.Empty(over.Chunks);
        Assert.Equal((CalendarChangeState.Applied, 0), (over.Process.State, over.Process.DayCount));
        Assert.NotNull(over.Process.FinishedAt);
    }

    [Fact]
    public async Task Record_AConfirmedMappingWithoutARoom_GetsFailedChunksWithAStableCode_AndNothingIsSent()
    {
        var h = new Push();
        h.Targets.Setup(t => t.LookupAsync(h.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.RoomNotChosen);

        var record = await h.Recorder().RecordAsync(
            CalendarChangeKind.Block, [new CalendarWindow(h.PropertyId, Push.Today.AddDays(1), Push.Today.AddDays(3))], null, null);

        var chunk = Assert.Single(record.Chunks);
        Assert.Equal((CalendarChunkState.Failed, "octorate_room_not_chosen"), (chunk.State, chunk.ErrorCode));
        Assert.Equal((CalendarChangeState.Failed, "octorate_room_not_chosen"), (record.Process.State, record.Process.ErrorCode));
        Assert.False(record.NeedsRun);
        Assert.Null(record.Process.NextAttemptAt);
    }

    [Fact]
    public async Task Record_WithTheConnectorOff_RecordsAnAppliedProcess_AndAsksTheChannelManagerNothing()
    {
        var h = new Push(connector: false);

        var record = await h.Recorder().RecordAsync(
            CalendarChangeKind.Block, [new CalendarWindow(h.PropertyId, Push.Today.AddDays(1), Push.Today.AddDays(3))], null, null);

        Assert.Empty(record.Chunks);
        Assert.Equal(CalendarChangeState.Applied, record.Process.State);
        h.Targets.Verify(t => t.LookupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Record_ALookupThatCannotBeAnswered_IsNotAReasonToRefuseTheChange_TheChunksAreRecordedPending()
    {
        var h = new Push();
        h.Targets.Setup(t => t.LookupAsync(h.PropertyId, It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(503));

        var record = await h.Recorder().RecordAsync(
            CalendarChangeKind.Block, [new CalendarWindow(h.PropertyId, Push.Today.AddDays(1), Push.Today.AddDays(3))], null, null);

        Assert.Equal(CalendarChunkState.Pending, Assert.Single(record.Chunks).State);
        Assert.Equal(CalendarChangeState.Queued, record.Process.State);
    }

    [Fact]
    public async Task Record_TheIdOfTheProcessThatIsGivenIsKept_AndAPropertyThatDoesNotExistIsNotFound()
    {
        var h = new Push();
        var id = Guid.NewGuid();

        var record = await h.Recorder().RecordAsync(
            CalendarChangeKind.Publication, [new CalendarWindow(h.PropertyId, Push.Today, Push.Today.AddDays(364))], null, null, processId: id);

        Assert.Equal(id, record.ProcessId);
        Assert.Equal(6, record.Chunks.Count);
        await Assert.ThrowsAsync<Casazen.Core.Exceptions.NotFoundException>(
            () => h.Recorder().RecordAsync(CalendarChangeKind.Block, [new CalendarWindow(Guid.NewGuid(), Push.Today, Push.Today.AddDays(1))], null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Recorder().RecordAsync(CalendarChangeKind.Block, [], null, null));
    }

    // --- The push ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_AProcessIdIsTheAcceptance_RecordedOnTheChunk_AndTheProcessIsSentNeverPublished()
    {
        var h = new Push();
        var (process, _) = await h.SeedAsync();
        h.Client
            .Setup(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarUpdateResult(["12345678"]));

        await h.Service().RunAsync(process.Id);

        var chunk = await h.ChunkAsync();
        var after = await h.ProcessAsync();
        Assert.Equal((CalendarChunkState.Accepted, "12345678", 1), (chunk.State, chunk.OctorateProcessId, chunk.Attempts));
        Assert.Equal(Push.Now.UtcDateTime, chunk.LastAttemptAt);
        Assert.Equal((CalendarChangeState.Sent, 1), (after.State, after.Attempts));
        Assert.Equal(Push.Now.UtcDateTime, after.StartedAt);
        // Accepted is not settled: the process is followed until the channel manager confirms it.
        Assert.Null(after.FinishedAt);
        Assert.Equal(Push.Now.UtcDateTime + CalendarPushRules.ConfirmationEvery, after.NextAttemptAt);
        Assert.Null(after.ErrorCode);
    }

    [Fact]
    public async Task Run_TheEntriesAreMadeAsTheNightsAreNow_TheDaysBeforeTodayCutOff_TheNightsOfAManualBlockClosed()
    {
        var h = new Push();
        h.Db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = h.PropertyId,
            OrgId = Push.OrgId,
            Source = CalendarBlockSource.Manual,
            StartUtc = Utc(Push.Today.AddDays(1)),
            EndUtc = Utc(Push.Today.AddDays(3)),
            ManualReason = CalendarBlockReason.Other,
        });
        await h.Db.SaveChangesAsync();
        var (process, _) = await h.SeedAsync(Push.Today.AddDays(-2), Push.Today.AddDays(3));
        var sent = new List<IReadOnlyList<OctorateCalendarUpdate>>();
        h.Client
            .Setup(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<OctorateCalendarUpdate>, CancellationToken>((updates, _) => sent.Add(updates))
            .ReturnsAsync(new OctorateCalendarUpdateResult(["1"]));

        await h.Service().RunAsync(process.Id);

        var entries = Assert.Single(sent);
        Assert.All(entries, e => Assert.Equal("956528", e.RoomId));
        Assert.Equal(Push.Today, entries[0].From);
        Assert.Equal(Push.Today.AddDays(3), entries[^1].To);
        var stopSells = Enumerable.Range(0, 4).Select(i => entries.Single(e => e.From <= Push.Today.AddDays(i) && Push.Today.AddDays(i) <= e.To).Values.StopSells);
        Assert.Equal([false, true, true, false], stopSells);
    }

    [Fact]
    public async Task Run_AQuotaThatIsNearlyUsed_PostponesTheChunk_WithoutFailingIt()
    {
        var h = new Push();
        var (process, _) = await h.SeedAsync();
        // 150 units used of 164, 20 kept free: the 1 unit of this chunk would pass the 144 that may be used.
        h.Client.Setup(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateConfiguration("SANDBOX", 164, 150, null));

        await h.Service().RunAsync(process.Id);

        var chunk = await h.ChunkAsync();
        var after = await h.ProcessAsync();
        Assert.Equal((CalendarChunkState.Pending, 0, (string?)null), (chunk.State, chunk.Attempts, chunk.ErrorCode));
        Assert.Equal((CalendarChangeState.Queued, "octorate_quota_low"), (after.State, after.ErrorCode));
        Assert.Equal(Push.Now.UtcDateTime.AddMinutes(5), after.NextAttemptAt);
        Assert.Null(after.FinishedAt);
        h.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_TheQuotaThatTheRunHasUsed_IsAddedToWhatIsLeft_SoTheNextChunkWaits()
    {
        var h = new Push();
        h.Options.MaxDaysPerCall = 5;
        var (process, _) = await h.SeedAsync(Push.Today.AddDays(1), Push.Today.AddDays(5));
        h.Db.CalendarChangeChunks.Add(
            new CalendarChangeChunk { OrgId = Push.OrgId, ProcessId = process.Id, PropertyId = h.PropertyId, FromDate = Push.Today.AddDays(6), ToDate = Push.Today.AddDays(10) });
        await h.Db.SaveChangesAsync();
        // 143 used of 164 with 20 free: one chunk of one unit fits (144), a second does not.
        h.Client.Setup(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateConfiguration("SANDBOX", 164, 143, null));
        h.Client
            .Setup(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCalendarUpdateResult(["1"]));

        await h.Service().RunAsync(process.Id);

        var chunks = await h.Db.CalendarChangeChunks.AsNoTracking().OrderBy(c => c.FromDate).ToListAsync();
        Assert.Equal([CalendarChunkState.Accepted, CalendarChunkState.Pending], chunks.Select(c => c.State));
        var after = await h.ProcessAsync();
        Assert.Equal((CalendarChangeState.Sending, "octorate_quota_low"), (after.State, after.ErrorCode));
        h.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Client.Verify(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_ARateLimitOrAServerError_KeepsTheChunkPending_WithABackoff_AndFailsItAfterTheFifthAttempt(bool rateLimit)
    {
        var h = new Push();
        var (process, _) = await h.SeedAsync();
        h.Client
            .Setup(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(rateLimit ? new OctorateRateLimitException(429) : new OctorateServerException(503));
        var backoffs = new List<int>();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await h.Service().RunAsync(process.Id);

            var chunk = await h.ChunkAsync();
            var after = await h.ProcessAsync();
            Assert.Equal((CalendarChunkState.Pending, attempt), (chunk.State, chunk.Attempts));
            Assert.Equal((CalendarChangeState.Queued, "octorate_unavailable"), (after.State, after.ErrorCode));
            backoffs.Add((int)(after.NextAttemptAt!.Value - Push.Now.UtcDateTime).TotalMinutes);
        }

        Assert.Equal([1, 2, 4, 8], backoffs);

        await h.Service().RunAsync(process.Id);

        var last = await h.ChunkAsync();
        var failed = await h.ProcessAsync();
        Assert.Equal((CalendarChunkState.Failed, "octorate_unavailable", 5), (last.State, last.ErrorCode, last.Attempts));
        Assert.Equal((CalendarChangeState.Failed, "octorate_unavailable"), (failed.State, failed.ErrorCode));
        Assert.Null(failed.NextAttemptAt);
        h.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    [Fact]
    public async Task Run_ARefusedAccess_FailsEveryPendingChunkOfTheProcess_AndNothingMoreLeaves()
    {
        var h = new Push();
        h.Options.MaxDaysPerCall = 5;
        var (process, _) = await h.SeedAsync(Push.Today.AddDays(1), Push.Today.AddDays(5));
        h.Db.CalendarChangeChunks.Add(
            new CalendarChangeChunk { OrgId = Push.OrgId, ProcessId = process.Id, PropertyId = h.PropertyId, FromDate = Push.Today.AddDays(6), ToDate = Push.Today.AddDays(10) });
        await h.Db.SaveChangesAsync();
        h.Client
            .Setup(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.Refused(403));

        await h.Service().RunAsync(process.Id);

        var chunks = await h.Db.CalendarChangeChunks.AsNoTracking().ToListAsync();
        Assert.All(chunks, c => Assert.Equal((CalendarChunkState.Failed, "octorate_not_authorized"), (c.State, c.ErrorCode)));
        Assert.Equal((CalendarChangeState.Failed, "octorate_not_authorized"), ((await h.ProcessAsync()).State, (await h.ProcessAsync()).ErrorCode));
        h.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ARefusalOfTheRequest_FailsOnlyThatChunk_AndTheNextOneIsSent()
    {
        var h = new Push();
        h.Options.MaxDaysPerCall = 5;
        var (process, _) = await h.SeedAsync(Push.Today.AddDays(1), Push.Today.AddDays(5));
        h.Db.CalendarChangeChunks.Add(
            new CalendarChangeChunk { OrgId = Push.OrgId, ProcessId = process.Id, PropertyId = h.PropertyId, FromDate = Push.Today.AddDays(6), ToDate = Push.Today.AddDays(10) });
        await h.Db.SaveChangesAsync();
        h.Client
            .SetupSequence(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRequestRejectedException(400))
            .ReturnsAsync(new OctorateCalendarUpdateResult(["7"]));

        await h.Service().RunAsync(process.Id);

        var chunks = await h.Db.CalendarChangeChunks.AsNoTracking().OrderBy(c => c.FromDate).ToListAsync();
        Assert.Equal([(CalendarChunkState.Failed, "octorate_request_rejected"), (CalendarChunkState.Accepted, (string?)null)], chunks.Select(c => (c.State, c.ErrorCode)));
        Assert.Equal(CalendarChangeState.PartiallySent, (await h.ProcessAsync()).State);
    }

    [Fact]
    public async Task Run_APropertyThatIsNotMirroredAnyMore_OrWhoseRoomIsNotChosen_IsNotSent()
    {
        var h = new Push();
        var (skipped, _) = await h.SeedAsync();
        h.Targets.Setup(t => t.LookupAsync(h.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.NotMirrored);
        await h.Service().RunAsync(skipped.Id);

        Assert.Equal((CalendarChunkState.Skipped, CalendarChangeState.Applied), ((await h.ChunkAsync()).State, (await h.ProcessAsync()).State));

        var h2 = new Push();
        var (waiting, _) = await h2.SeedAsync();
        h2.Targets.Setup(t => t.LookupAsync(h2.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.RoomNotChosen);
        await h2.Service().RunAsync(waiting.Id);

        var chunk = await h2.ChunkAsync();
        Assert.Equal((CalendarChunkState.Failed, "octorate_room_not_chosen"), (chunk.State, chunk.ErrorCode));
        Assert.Equal(CalendarChangeState.Failed, (await h2.ProcessAsync()).State);
        h.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Never);
        h2.Client.Verify(c => c.UpdateCalendarAsync(It.IsAny<IReadOnlyList<OctorateCalendarUpdate>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_WhileTheConnectorIsOff_SendsNothing_AndTheChunksWait()
    {
        var h = new Push(connector: false);
        var (process, _) = await h.SeedAsync();

        await h.Service().RunAsync(process.Id);

        Assert.Equal(CalendarChunkState.Pending, (await h.ChunkAsync()).State);
        Assert.Equal(CalendarChangeState.Queued, (await h.ProcessAsync()).State);
        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Run_AProcessThatIsNotToSend_OrDoesNotExist_IsLeftAlone()
    {
        var h = new Push();
        var (process, _) = await h.SeedAsync(processState: CalendarChangeState.Sent, chunkState: CalendarChunkState.Accepted);

        await h.Service().RunAsync(process.Id);
        await h.Service().RunAsync(Guid.NewGuid());

        Assert.Equal(CalendarChunkState.Accepted, (await h.ChunkAsync()).State);
        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Run_AFailureThatIsNotTheChannelManagers_GoesUp_AndTheProcessIsLeftWithALaterAttempt()
    {
        var h = new Push();
        var (process, _) = await h.SeedAsync();
        h.Rules.Setup(r => r.GetRulesAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("a bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service().RunAsync(process.Id));

        var after = await h.ProcessAsync();
        Assert.Equal(CalendarChangeState.Queued, after.State);
        Assert.Equal((1, Push.Now.UtcDateTime.AddMinutes(1)), (after.Attempts, after.NextAttemptAt));
        Assert.Equal(CalendarChunkState.Pending, (await h.ChunkAsync()).State);
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------------------

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static OctorateListingMapping Mapping(
        Guid connectionId,
        string accommodation,
        OctorateMappingStatus status,
        Guid? propertyId,
        string? productId,
        string? candidates = null)
    {
        return new OctorateListingMapping
        {
            OrgId = Push.OrgId,
            ConnectionId = connectionId,
            OctorateAccommodationId = accommodation,
            OctorateProductId = productId,
            SnapshotName = "Listing " + accommodation,
            Status = status,
            PropertyId = propertyId,
            CandidatePropertyIdsJson = candidates ?? "[]",
        };
    }

    private sealed class Push
    {
        public static readonly Guid OrgId = Guid.Parse("1b3f5c0e-2a14-4e8d-9d52-7a6c4b1e0f01");

        // 9 October 2026, 10:00 UTC: the 9th in Rome.
        public static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

        public static readonly DateOnly Today = new(2026, 10, 9);

        private static readonly IReadOnlyDictionary<DateOnly, DayRuleValues> NoRules = new Dictionary<DateOnly, DayRuleValues>();

        private static readonly IReadOnlyDictionary<DateOnly, decimal> NoPrices = new Dictionary<DateOnly, decimal>();

        private readonly string _database = $"calendar-push-{Guid.NewGuid():N}";

        public Push(bool connector = true)
        {
            Db = NewDb();
            Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(connector);
            PropertyId = AddProperty("Casa del Lago");
            Targets
                .Setup(t => t.LookupAsync(PropertyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("ACC1", "956528")));
            Rules
                .Setup(r => r.GetRulesAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NoRules);
            Pricing
                .Setup(p => p.GetAppliedNightlyPricesAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(NoPrices);
            Client.Setup(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateConfiguration("SANDBOX", 164, 10, null));
        }

        public AppDbContext Db { get; }

        public Mock<IOctorateApiClient> Client { get; } = new();

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public Mock<IPropertyDayRuleService> Rules { get; } = new();

        public Mock<IPricingAdapterService> Pricing { get; } = new();

        public Mock<IFeatureFlags> Flags { get; } = new();

        public OctorateCalendarOptions Options { get; } = new();

        public Guid PropertyId { get; }

        /// <summary>A second context on the same database: what is saved, whatever the first one still tracks.</summary>
        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

        public Guid AddProperty(string name)
        {
            var property = HostScopeScenario.NewProperty(OrgId, "auth0|owner", name);
            Db.Properties.Add(property);
            Db.SaveChanges();
            return property.Id;
        }

        public CalendarPushService Service(IOctorateBookingTargets? targets = null) =>
            new(
                Db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                targets ?? Targets.Object,
                Rules.Object,
                Pricing.Object,
                Flags.Object,
                new ConfigurationBuilder().Build(),
                Microsoft.Extensions.Options.Options.Create(Options),
                new FixedTimeProvider(Now),
                NullLogger<CalendarPushService>.Instance);

        public CalendarChangeRecorder Recorder(IOctorateBookingTargets? targets = null) =>
            new(
                Db,
                targets ?? Targets.Object,
                Flags.Object,
                Microsoft.Extensions.Options.Options.Create(Options),
                new FixedTimeProvider(Now),
                NullLogger<CalendarChangeRecorder>.Instance);

        /// <summary>The real target lookup over the database of the test (the mapping rows decide), with the channel mapping given.</summary>
        public OctorateBookingTargets RealTargets(IChannelMappingService mappings)
        {
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            return new OctorateBookingTargets(
                Db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                mappings,
                new MemoryCache(new MemoryCacheOptions()),
                host.Object,
                new FixedTimeProvider(Now),
                NullLogger<OctorateBookingTargets>.Instance);
        }

        /// <summary>A process waiting to be sent with one chunk (a pending one by default) for the property; the tracker is cleared, so the service reads what is stored.</summary>
        public async Task<(CalendarChangeProcess Process, CalendarChangeChunk Chunk)> SeedAsync(
            DateOnly? from = null,
            DateOnly? to = null,
            CalendarChangeState processState = CalendarChangeState.Queued,
            CalendarChunkState chunkState = CalendarChunkState.Pending)
        {
            var first = from ?? Today.AddDays(1);
            var last = to ?? Today.AddDays(3);
            var process = new CalendarChangeProcess
            {
                OrgId = OrgId,
                Kind = CalendarChangeKind.Block,
                State = processState,
                PropertyCount = 1,
                DayCount = last.DayNumber - first.DayNumber + 1,
                SummaryJson = CalendarChangeSummary.Build([new CalendarWindow(PropertyId, first, last)], null),
                CreatedAt = Now.UtcDateTime,
                NextAttemptAt = Now.UtcDateTime,
            };
            var chunk = new CalendarChangeChunk { OrgId = OrgId, ProcessId = process.Id, PropertyId = PropertyId, FromDate = first, ToDate = last, State = chunkState };
            Db.CalendarChangeProcesses.Add(process);
            Db.CalendarChangeChunks.Add(chunk);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return (process, chunk);
        }

        public async Task<CalendarChangeChunk> ChunkAsync() => await NewDb().CalendarChangeChunks.AsNoTracking().OrderBy(c => c.FromDate).FirstAsync();

        public async Task<CalendarChangeProcess> ProcessAsync() => await NewDb().CalendarChangeProcesses.AsNoTracking().OrderByDescending(p => p.CreatedAt).FirstAsync();
    }
}
