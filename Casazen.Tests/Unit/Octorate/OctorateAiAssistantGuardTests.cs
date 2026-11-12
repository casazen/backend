using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E05/F05/US03/T03 (NF-67, OCT-11, AC-NF67-6): Octorate's own assistant is kept off on the threads CasaZen manages. The consumer of
/// <c>OtaThreadSynced</c> switches it off when the pass read it on (or did not read it), costs no call for a thread that is off, does not fail the
/// event for a refusal no retry changes, and lets the dispatcher deliver it again for what a retry can change (the reserve of the calls, a 5xx, a 429); the
/// passes of the copy find a person who switched it on again, and repeat a call that failed. The client of
/// Octorate is a strict stub the test sets up (the sandbox has no chat license, so the wire is not what is proven here); the database is in memory.
/// </summary>
public class OctorateAiAssistantGuardTests
{
    private const string OctorateThread = "9001";

    /// <summary>
    /// PROOF TEST: data rule (AC-NF67-6, AC1). A thread whose assistant of Octorate reads on is switched off with ONE call for the organization of the
    /// event, the copy says off afterwards, and the same event delivered again (at-least-once) costs no second call.
    /// </summary>
    [Fact]
    public async Task OctorateAssistantOn_IsSwitchedOff_OnOneCallForTheOrgOfTheEvent_AndTheCopySaysOff_Test()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var calls = RecordCalls(h, scope);
        var thread = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId);
        var log = new CapturingLogger<OctorateAiAssistantGuard>();
        var synced = Synced(thread, ai: true);

        await DeliverAsync(h, scope, log, synced);

        Assert.Equal([(MessageSyncHarness.Accommodation, OctorateThread, false, (Guid?)MessageSyncHarness.OrgA)], calls);
        var row = await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync());
        Assert.False(row.AiAssistantOn);
        Assert.Equal(MessageSyncHarness.Now.UtcDateTime, row.UpdatedAt);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && Equals(e.Values["ThreadId"], thread.Id));

        // The dispatcher delivers an event at least once: the second delivery finds the copy off and calls nothing.
        await DeliverAsync(h, scope, log, synced);

        Assert.Single(calls);
    }

    [Fact]
    public async Task AFlagOctorateDoesNotSay_OnTheFirstPass_IsSwitchedOffToo()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var calls = RecordCalls(h, scope);
        var thread = await SeedThreadAsync(h, ai: null, propertyId: h.PropertyId);

        await DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(thread, ai: null));

        Assert.Single(calls);
        Assert.False((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    /// <summary>AC2: a thread that is off costs no call, whether the pass read it off or the copy already holds it off.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task AThreadThatIsOff_CostsNoCall(bool? payloadSays, bool? copyHolds)
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var calls = RecordCalls(h, scope);
        var thread = await SeedThreadAsync(h, ai: copyHolds, propertyId: h.PropertyId);

        await DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(thread, ai: payloadSays));

        Assert.Empty(calls);
        Assert.Equal(copyHolds, (await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    /// <summary>
    /// PROOF TEST: data rule (AC-NF67-6, AC3, AC4). Over the real sync and the real consumer, pass after pass: a person who switches the assistant on
    /// again is switched off again at the next pass (the key of the event of a thread whose assistant is on has the ten minutes of the pass in it, so
    /// the first event does not take it for ever), a call that failed is repeated at the next pass, the pass of the same ten minutes is one event, and a
    /// pass that reads it off calls nothing.
    /// </summary>
    [Fact]
    public async Task ThePassesOfTheCopy_FindAPersonWhoSwitchedTheAssistantOnAgain_AndRepeatACallThatFailed_Test()
    {
        var h = new MessageSyncHarness();
        var clock = new FakeTimeProvider(MessageSyncHarness.Now);
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var log = new CapturingLogger<OctorateAiAssistantGuard>();
        var calls = RecordCalls(h, scope);
        bool? remote = true;
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, OctorateThread, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => MessageSyncHarness.Thread(OctorateThread) with { AiDriven = remote });
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, OctorateThread, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", OctorateThread)]);
        var dispatched = new HashSet<Guid>();

        async Task PassAsync()
        {
            await using (var db = h.NewDb())
            {
                var service = new MessageSyncService(
                    db,
                    h.Client.Object,
                    new OctorateOrgScope(NullTenantContext.Instance),
                    h.Mappings.Object,
                    new DomainEventRecorder(db, NullTenantContext.Instance, clock),
                    h.Flags.Object,
                    h.Limiter,
                    new MessageSyncPacing(),
                    clock,
                    h.Log);
                await service.SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, OctorateThread, null, CancellationToken.None);
            }

            // What the dispatcher does: every event that was not delivered yet, in its order, to the handler, on a context of its own.
            List<OutboxEvent> rows;
            await using (var read = h.NewDb())
            {
                rows = await read.OutboxEvents
                    .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                    .AsNoTracking()
                    .Where(e => e.EventType == DomainEventType.OtaThreadSynced)
                    .OrderBy(e => e.OccurredAt)
                    .ToListAsync();
            }

            foreach (var row in rows.Where(r => dispatched.Add(r.Id)))
                await DeliverAsync(h, scope, log, DomainEventTypeMap.Create(row), clock);
        }

        Task<int> EventsAsync() => h.ReadAsync(db => db.OutboxEvents.CountAsync(e => e.EventType == DomainEventType.OtaThreadSynced));
        Task<bool?> CopyAsync() => h.ReadAsync(async db => (await db.OtaMessageThreads.SingleAsync()).AiAssistantOn);

        // 08:00, the first pass reads the assistant on: one event, one call, the copy says off.
        await PassAsync();
        Assert.Equal(1, await EventsAsync());
        Assert.Single(calls);
        Assert.False(await CopyAsync());

        // 08:03, the webhook of the same ten minutes reads it on again (Octorate had not applied the call yet): the same event, so nothing is delivered.
        clock.Advance(TimeSpan.FromMinutes(3));
        await PassAsync();
        Assert.Equal(1, await EventsAsync());
        Assert.Single(calls);
        Assert.True(await CopyAsync());

        // 08:10, the next pass reads it on again, a person switched it on after the call: a new event, a second call, the copy says off.
        clock.Advance(TimeSpan.FromMinutes(7));
        await PassAsync();
        Assert.Equal(2, await EventsAsync());
        Assert.Equal(2, calls.Count);
        Assert.False(await CopyAsync());

        // 08:20, Octorate holds it off: the event of an assistant that is off, and no call.
        clock.Advance(TimeSpan.FromMinutes(10));
        remote = false;
        await PassAsync();
        Assert.Equal(3, await EventsAsync());
        Assert.Equal(2, calls.Count);
        Assert.False(await CopyAsync());

        // 08:30, a person switched it on again and Octorate says the quota of the day is used up (a refusal no retry changes, a minute does not refill it): it is
        // logged, the event is not failed, the copy keeps what Octorate said, and the next pass is the retry. (What a retry can change rises, see below.)
        clock.Advance(TimeSpan.FromMinutes(10));
        remote = true;
        h.Client.Setup(c => c.SetThreadAiAssistantAsync(MessageSyncHarness.Accommodation, OctorateThread, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(403, quotaExhausted: true));
        await PassAsync();
        Assert.Equal(4, await EventsAsync());
        Assert.True(await CopyAsync());
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && Equals(e.Values["Code"], "octorate_rate_limited"));

        // 08:40, the next pass reads it on still: the call is repeated at the next pass, and now Octorate accepts it.
        clock.Advance(TimeSpan.FromMinutes(10));
        h.Client.Setup(c => c.SetThreadAiAssistantAsync(MessageSyncHarness.Accommodation, OctorateThread, false, It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, CancellationToken>((accommodation, thread, enabled, _) => calls.Add((accommodation, thread, enabled, scope.OrgId)))
            .Returns(Task.CompletedTask);
        await PassAsync();
        Assert.Equal(5, await EventsAsync());
        Assert.Equal(3, calls.Count);
        Assert.False(await CopyAsync());

        // Ids and codes only: the thread of the sync carries the name of a guest, and no line of the guard has it.
        Assert.DoesNotContain("Giulietta", log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdi", log.AllOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: privacy and data rules (AC4). A refusal that no retry changes (no chat module, not connected, the thread gone, the request or the token
    /// refused, the quota of the day used up) does not fail the event: the log carries the thread, the organization and the code (no name of the guest,
    /// nothing of the answer), and the copy keeps the flag Octorate reported, so the next pass tries again.
    /// </summary>
    [Theory]
    [InlineData("quota", "octorate_rate_limited")]
    [InlineData("chat", "chat_not_licensed")]
    [InlineData("notconnected", "octorate_not_connected")]
    [InlineData("notfound", "octorate_not_found")]
    [InlineData("rejected", "octorate_request_rejected")]
    [InlineData("auth", "octorate_auth_failed")]
    public async Task AFailedCall_IsLoggedWithIdsAndTheCodeOnly_NeverFailsTheEvent_AndLeavesTheFlag_Test(string failure, string code)
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        h.Client
            .Setup(c => c.SetThreadAiAssistantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure switch
            {
                "quota" => new OctorateRateLimitException(403, quotaExhausted: true),
                "chat" => OctorateAuthException.ChatNotLicensed(403),
                "notconnected" => OctorateAuthException.NotConnected(),
                "notfound" => new OctorateNotFoundException(),
                "rejected" => new OctorateRequestRejectedException(400),
                _ => OctorateAuthException.Refused(403),
            });
        var thread = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId);
        var log = new CapturingLogger<OctorateAiAssistantGuard>();

        // Not an exception: the dispatcher marks the event delivered, and the sync and the other handlers are not affected.
        await DeliverAsync(h, scope, log, Synced(thread, ai: true));

        Assert.True((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(thread.Id, warning.Values["ThreadId"]);
        Assert.Equal(MessageSyncHarness.OrgA, warning.Values["OrgId"]);
        Assert.Equal(code, warning.Values["Code"]);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain("Giulietta", log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdi", log.AllOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// What is not a refusal of Octorate is not swallowed: it rises, so the dispatcher delivers the event again (the call is idempotent), and the copy is
    /// not changed for a call nobody knows the outcome of.
    /// </summary>
    [Fact]
    public async Task AnErrorThatIsNotOctorates_IsNotSwallowed_SoTheEventIsDeliveredAgain()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        h.Client
            .Setup(c => c.SetThreadAiAssistantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("a bug"));
        var thread = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(thread, ai: true)));

        Assert.True((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    /// <summary>
    /// PROOF TEST: permission rule (OCT-04). Nothing is written to the channel manager for a thread CasaZen does not manage: one with no property (its listing
    /// has no confirmed mapping) and one whose property sits in a mapping nobody decided. The flag the copy holds stays what Octorate reported.
    /// </summary>
    [Fact]
    public async Task AThreadOfAListingNobodyConfirmed_IsNeverWrittenTo_Test()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var calls = RecordCalls(h, scope);
        var unassigned = await SeedThreadAsync(h, ai: true, propertyId: null);

        await DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(unassigned, ai: true));

        var undecided = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId, octorateThreadId: "9002");
        h.Mappings.Setup(m => m.IsWriteBlockedAsync(h.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(undecided, ai: true));

        Assert.Empty(calls);
        Assert.All(await h.ReadAsync(db => db.OtaMessageThreads.ToListAsync()), row => Assert.True(row.AiAssistantOn));

        // The mapping is decided: the same event now switches it off.
        h.Mappings.Setup(m => m.IsWriteBlockedAsync(h.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(undecided, ai: true));

        Assert.Equal([(MessageSyncHarness.Accommodation, "9002", false, (Guid?)MessageSyncHarness.OrgA)], calls);
    }

    /// <summary>
    /// PROOF TEST: data rule (AC-NF67-6, AC4). The pass leaves a few calls of the bucket to the people who use CasaZen: with the bucket down to that reserve
    /// nothing is sent, and the event is NOT consumed: the handler rises, so the dispatcher keeps it and delivers it again. The thread here is dormant
    /// (nobody wrote to it for five days, as the threads of the import of a history): the pass of every 10 minutes reads the last day and the deep pass
    /// the last two, so no pass would ever tell the guard of it again, and the redelivery is its one retry. Once the calls are back the same event switches it off.
    /// </summary>
    [Fact]
    public async Task TheCallsOfTheOrgDownToTheReserve_AreLeftToThePeopleWhoUseCasaZen_AndTheEventOfADormantThreadIsDeliveredAgain_Test()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var clock = new FakeTimeProvider(MessageSyncHarness.Now);
        var limiter = new OctorateRateLimiter(clock);
        var calls = RecordCalls(h, scope);
        var thread = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId, lastMessageAt: MessageSyncHarness.Now.UtcDateTime.AddDays(-5));
        Assert.True(thread.LastMessageAt < MessageSyncHarness.Now.UtcDateTime - MessageSyncService.MinDeepOverlap);
        Assert.True(thread.LastMessageAt < MessageSyncHarness.Now.UtcDateTime - MessageSyncService.RecentFloor);
        while (limiter.AvailableCalls(MessageSyncHarness.OrgA) > MessageSyncService.RateReserve)
            Assert.True(await limiter.TryAcquireAsync(MessageSyncHarness.OrgA));
        var log = new CapturingLogger<OctorateAiAssistantGuard>();
        var synced = Synced(thread, ai: true);

        // Down to the reserve: no call, the event rises (the dispatcher keeps it for its next run), the copy keeps what Octorate said, the line has ids and the code.
        var raised = await Assert.ThrowsAsync<OctorateRateLimitException>(() => DeliverAsync(h, scope, log, synced, limiter: limiter));

        Assert.Equal(OctorateAiAssistantGuard.RateLimitedCode, raised.Code);
        Assert.Empty(calls);
        Assert.True((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
        var line = Assert.Single(log.Entries);
        Assert.Equal(OctorateAiAssistantGuard.RateLimitedCode, line.Values["Code"]);
        Assert.Equal(thread.Id, line.Values["ThreadId"]);
        Assert.Equal(MessageSyncHarness.OrgA, line.Values["OrgId"]);

        // The next run of the dispatcher, the calls of the org are back: the same event makes the call, and the copy says off.
        clock.Advance(TimeSpan.FromMinutes(1));
        await DeliverAsync(h, scope, log, synced, limiter: limiter);

        Assert.Equal([(MessageSyncHarness.Accommodation, OctorateThread, false, (Guid?)MessageSyncHarness.OrgA)], calls);
        Assert.False((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    /// <summary>
    /// PROOF TEST: privacy and data rules (AC4). A refusal a retry can change (Octorate fails or does not answer, the bucket is empty or a 429 comes after the
    /// waits) is logged with the thread, the organization and the code (no name of the guest, no exception object) and then rises, so the dispatcher delivers
    /// the event again, whatever the flag of the pass (on, or not said); the copy keeps what Octorate reported, and the same event succeeds when Octorate answers.
    /// </summary>
    [Theory]
    [InlineData("server", "octorate_unavailable", true)]
    [InlineData("server", "octorate_unavailable", null)]
    [InlineData("rate", "octorate_rate_limited", true)]
    [InlineData("rate", "octorate_rate_limited", null)]
    public async Task AFailedCallARetryCanChange_IsLoggedWithIdsAndTheCodeOnly_AndRises_SoTheEventIsDeliveredAgain_Test(string failure, string code, bool? flag)
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        Exception refusal = failure == "server" ? new OctorateServerException(500) : new OctorateRateLimitException(429);
        h.Client
            .Setup(c => c.SetThreadAiAssistantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(refusal);
        var thread = await SeedThreadAsync(h, ai: flag, propertyId: h.PropertyId);
        var log = new CapturingLogger<OctorateAiAssistantGuard>();
        var synced = Synced(thread, ai: flag);

        var raised = await Assert.ThrowsAnyAsync<OctorateException>(() => DeliverAsync(h, scope, log, synced));

        Assert.Equal(code, raised.Code);
        Assert.Equal(flag, (await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(thread.Id, warning.Values["ThreadId"]);
        Assert.Equal(MessageSyncHarness.OrgA, warning.Values["OrgId"]);
        Assert.Equal(code, warning.Values["Code"]);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain("Giulietta", log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdi", log.AllOutput, StringComparison.Ordinal);

        // The dispatcher delivers the same event again; Octorate answers now: one call, the copy says off.
        var calls = RecordCalls(h, scope);
        await DeliverAsync(h, scope, log, synced);

        Assert.Equal([(MessageSyncHarness.Accommodation, OctorateThread, false, (Guid?)MessageSyncHarness.OrgA)], calls);
        Assert.False((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    /// <summary>
    /// PROOF TEST: data rule (AC-NF67-6, AC4). Over the real sync and the real consumer: for a flag Octorate does not say the key of the event has no window, so
    /// the next pass records no second event and does not tell the guard again; the dispatcher is the only retry of a call that failed, and it works (the
    /// proof of the passes covers the flag that reads on, where the next pass is the retry).
    /// </summary>
    [Fact]
    public async Task ForAFlagOctorateDoesNotSay_ACallThatFailed_IsRetriedByTheDispatcher_NotByTheNextPass_Test()
    {
        var h = new MessageSyncHarness();
        var clock = new FakeTimeProvider(MessageSyncHarness.Now);
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var log = new CapturingLogger<OctorateAiAssistantGuard>();
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, OctorateThread, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread(OctorateThread) with { AiDriven = null });
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, OctorateThread, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", OctorateThread)]);
        h.Client
            .Setup(c => c.SetThreadAiAssistantAsync(MessageSyncHarness.Accommodation, OctorateThread, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));

        async Task<List<OutboxEvent>> PassAsync()
        {
            await using (var db = h.NewDb())
            {
                var service = new MessageSyncService(
                    db,
                    h.Client.Object,
                    new OctorateOrgScope(NullTenantContext.Instance),
                    h.Mappings.Object,
                    new DomainEventRecorder(db, NullTenantContext.Instance, clock),
                    h.Flags.Object,
                    h.Limiter,
                    new MessageSyncPacing(),
                    clock,
                    h.Log);
                await service.SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, OctorateThread, null, CancellationToken.None);
            }

            await using var read = h.NewDb();
            return await read.OutboxEvents
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(e => e.EventType == DomainEventType.OtaThreadSynced)
                .ToListAsync();
        }

        // 08:00, the first pass reads no flag: one event; Octorate fails the call, the guard says so and the event rises (the dispatcher keeps it).
        var first = Assert.Single(await PassAsync());
        var raised = await Assert.ThrowsAsync<OctorateServerException>(() => DeliverAsync(h, scope, log, DomainEventTypeMap.Create(first), clock));
        Assert.Equal("octorate_unavailable", raised.Code);
        Assert.Null((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);

        // 08:10, the next pass reads no flag again: the key has no window, so it is the same event and the pass tells the guard nothing more.
        clock.Advance(TimeSpan.FromMinutes(10));
        var second = Assert.Single(await PassAsync());
        Assert.Equal(first.Id, second.Id);

        // The dispatcher delivers the event again, Octorate answers: one call, the copy says off.
        var calls = RecordCalls(h, scope);
        await DeliverAsync(h, scope, log, DomainEventTypeMap.Create(first), clock);

        Assert.Equal([(MessageSyncHarness.Accommodation, OctorateThread, false, (Guid?)MessageSyncHarness.OrgA)], calls);
        Assert.False((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    /// <summary>
    /// PROOF TEST: tenant rule (ADR-022-R6). An event of one organization that names the thread of another changes nothing and calls nothing: the handler
    /// reads the thread in the org of the event, and the call is made for that org.
    /// </summary>
    [Fact]
    public async Task AnEventOfAnotherOrg_NeverReachesTheThreadOfThisOne_Test()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var calls = RecordCalls(h, scope);
        var thread = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId);

        await DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(thread, ai: true, org: MessageSyncHarness.OrgB));

        Assert.Empty(calls);
        Assert.True((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    [Fact]
    public async Task WhileTheConnectorIsOff_AnotherEvent_NoEntity_OrAPayloadThatIsNotOne_NothingHappens()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var calls = RecordCalls(h, scope);
        var thread = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId);
        var log = new CapturingLogger<OctorateAiAssistantGuard>();
        var synced = Synced(thread, ai: true);

        // Another kind of event, and an event that names nothing.
        await DeliverAsync(h, scope, log, synced with { Type = DomainEventType.GuestMessageReceived });
        await DeliverAsync(h, scope, log, synced with { EntityId = Guid.Empty });
        Assert.Empty(log.Entries);

        // A payload that is not the one of the pass: said, and nothing is called.
        foreach (var payload in new[] { "not json", "[]", "null", "42" })
            await DeliverAsync(h, scope, log, synced with { PayloadJson = payload });
        Assert.Equal(4, log.Entries.Count(e => e.Level == LogLevel.Warning && Equals(e.Values["Code"], OctorateAiAssistantGuard.PayloadUnreadableCode)));

        // The connector off.
        h.Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(false);
        await DeliverAsync(h, scope, log, synced);

        Assert.Empty(calls);
        Assert.True((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).AiAssistantOn);
    }

    [Fact]
    public async Task AThreadThatIsNotThereAnyMore_IsNoFailure_AndCallsNothing()
    {
        var h = new MessageSyncHarness();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var calls = RecordCalls(h, scope);
        var thread = await SeedThreadAsync(h, ai: true, propertyId: h.PropertyId);
        await using (var db = h.NewDb())
        {
            db.OtaMessageThreads.Remove(await db.OtaMessageThreads.SingleAsync());
            await db.SaveChangesAsync();
        }

        await DeliverAsync(h, scope, new CapturingLogger<OctorateAiAssistantGuard>(), Synced(thread, ai: true));

        Assert.Empty(calls);
    }

    // ---- The rig ----------------------------------------------------------------------------------------------------------------------------

    private static async Task DeliverAsync(
        MessageSyncHarness h,
        OctorateOrgScope scope,
        CapturingLogger<OctorateAiAssistantGuard> log,
        DomainEvent domainEvent,
        TimeProvider? clock = null,
        OctorateRateLimiter? limiter = null)
    {
        // One context for each delivery, as the event bus gives each handler its scope.
        await using var db = h.NewDb();
        var guard = new OctorateAiAssistantGuard(db, h.Client.Object, scope, h.Mappings.Object, h.Flags.Object, limiter ?? h.Limiter, clock ?? h.Clock, log);
        await guard.HandleAsync(domainEvent, CancellationToken.None);
    }

    /// <summary>Sets the call of the client up: what it was asked (and for which org, read from the scope at the moment of the call) goes to the list.</summary>
    private static List<(string Accommodation, string Thread, bool Enabled, Guid? Org)> RecordCalls(MessageSyncHarness h, OctorateOrgScope scope)
    {
        var calls = new List<(string Accommodation, string Thread, bool Enabled, Guid? Org)>();
        h.Client
            .Setup(c => c.SetThreadAiAssistantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, CancellationToken>((accommodation, thread, enabled, _) => calls.Add((accommodation, thread, enabled, scope.OrgId)))
            .Returns(Task.CompletedTask);
        return calls;
    }

    /// <summary>A thread of Octorate as the copy holds it: the property its confirmed listing gives (or none), the flag of Octorate's assistant the pass read.</summary>
    private static async Task<OtaMessageThread> SeedThreadAsync(MessageSyncHarness h, bool? ai, Guid? propertyId, string octorateThreadId = OctorateThread, DateTime? lastMessageAt = null)
    {
        var thread = new OtaMessageThread
        {
            OrgId = MessageSyncHarness.OrgA,
            PropertyId = propertyId,
            OctorateThreadId = octorateThreadId,
            OctorateAccommodationId = MessageSyncHarness.Accommodation,
            ChannelSource = OtaChannel.Airbnb,
            Kind = OtaThreadKind.Reservation,
            GuestDisplayName = "Giulietta Verdi",
            LastMessageAt = lastMessageAt ?? MessageSyncHarness.Now.UtcDateTime.AddHours(-1),
            AiAssistantOn = ai,
            CreatedAt = MessageSyncHarness.Now.UtcDateTime.AddDays(-1),
            UpdatedAt = MessageSyncHarness.Now.UtcDateTime.AddDays(-1),
        };
        await using var db = h.NewDb();
        db.OtaMessageThreads.Add(thread);
        await db.SaveChangesAsync();
        return thread;
    }

    /// <summary>The event the sync records for a thread it read: the thread, and what the pass read of the assistant.</summary>
    private static DomainEvent Synced(OtaMessageThread thread, bool? ai, Guid? org = null) => new(
        Guid.NewGuid(),
        org ?? thread.OrgId,
        DomainEventType.OtaThreadSynced,
        thread.Id,
        MessageSyncHarness.Now.UtcDateTime,
        JsonSerializer.Serialize(new OtaThreadSyncedPayload($"ota:{thread.Id:D}", thread.OctorateThreadId, thread.OctorateAccommodationId, thread.PropertyId, ai)));
}
