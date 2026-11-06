using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the sync of the OTA conversations (E05/F01/US01/T02, ADR-032, ADR-022): a message is stored once however many times it is
/// delivered, the logs never carry the words of a guest, a thread of a listing nobody confirmed belongs to no property, and a license without the
/// chat module is a state the host sees and not an empty inbox. The client of Octorate is a strict stub the test sets up (the sandbox has no chat
/// license, so the wire is not what is proven here); the database is in memory.
/// </summary>
public class MessageSyncProofTests
{
    private const string ThreadId = "9001";

    /// <summary>
    /// PROOF TEST: data rule (ADR-022-R7, AC-NF62-2). The same message delivered twice, by two services on two contexts as the webhook and the
    /// reconciliation would, is one row and one event of each kind; what Octorate adds to it later (the guest read it) completes the row and does
    /// not make a second one.
    /// </summary>
    [Fact]
    public async Task OtaMessageSync_RepeatedDelivery_StoresOneMessage_Test()
    {
        var h = new MessageSyncHarness();
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread(ThreadId));
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);

        SyncResult first;
        SyncResult second;
        await using (var db = h.NewDb())
            first = await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, ThreadId, null, CancellationToken.None);
        await using (var db = h.NewDb())
            second = await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, ThreadId, null, CancellationToken.None);

        Assert.Equal(new SyncResult(1, 1, 0, 0), first);
        Assert.Equal(new SyncResult(1, 0, 0, 0), second);

        var (threads, messages, received, synced, thread) = await h.ReadAsync(async db => (
            await db.OtaMessageThreads.CountAsync(),
            await db.OtaMessages.CountAsync(),
            await db.OutboxEvents.CountAsync(e => e.EventType == DomainEventType.GuestMessageReceived),
            await db.OutboxEvents.CountAsync(e => e.EventType == DomainEventType.OtaThreadSynced),
            await db.OtaMessageThreads.SingleAsync()));
        Assert.Equal((1, 1, 1, 1), (threads, messages, received, synced));
        Assert.Equal(h.PropertyId, thread.PropertyId);
        Assert.Equal(OtaChannel.Airbnb, thread.ChannelSource);
        Assert.Equal(OtaThreadKind.Reservation, thread.Kind);
        Assert.Equal(1, thread.UnreadCount);
        Assert.Equal(OtaMessageDirection.Inbound, thread.LastMessageDirection);

        // The third delivery: the same message, now read by the host side on the channel manager. Still one row, now read, and no new event.
        var read = MessageSyncHarness.Inbound("70001", ThreadId) with { ReadAt = MessageSyncHarness.Now.UtcDateTime.AddMinutes(-30) };
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([read]);
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, ThreadId, null, CancellationToken.None);

        var (rows, row, events, unread) = await h.ReadAsync(async db => (
            await db.OtaMessages.CountAsync(),
            await db.OtaMessages.SingleAsync(),
            await db.OutboxEvents.CountAsync(e => e.EventType == DomainEventType.GuestMessageReceived),
            (await db.OtaMessageThreads.SingleAsync()).UnreadCount));
        Assert.Equal((1, 1, 0), (rows, events, unread));
        Assert.Equal(read.ReadAt, row.ReadAt);

        // The event says where the message came from by ids only: the thread key, the property, the booking is not there yet, the channel.
        var payload = await h.ReadAsync(async db =>
            (await db.OutboxEvents.SingleAsync(e => e.EventType == DomainEventType.GuestMessageReceived)).PayloadJson);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal($"ota:{thread.Id:D}", document.RootElement.GetProperty("threadKey").GetString());
        Assert.Equal("Ota", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("Airbnb", document.RootElement.GetProperty("channel").GetString());
        Assert.Equal(h.PropertyId, document.RootElement.GetProperty("propertyId").GetGuid());
        Assert.DoesNotContain("Giulietta", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check-in", payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PROOF TEST: privacy rule (ADR-032, ADR-022-R21). A run of the sync, a run of the reconciliation and a run that fails log ids, codes and counts:
    /// not the words of the message, not the name of the guest, not the address of the attachment, not the e-mail the guest wrote in it.
    /// </summary>
    [Fact]
    public async Task OtaMessageSync_Logs_NeverContainMessageText_Test()
    {
        const string Words = "Il mio numero di carta 4111 111111 111111 scade a 12/29, scrivetemi a giulietta.verdi@example.com";
        const string Name = "Giulietta Verdi";
        const string Attachment = "https://resx.example.test/chat/foto-segreta-998877.jpg";
        string[] secrets = [Words, "4111", Name, "Giulietta", "giulietta.verdi", Attachment, "foto-segreta"];

        var h = new MessageSyncHarness();
        var message = MessageSyncHarness.Inbound("70001", ThreadId, Words) with
        {
            AttachmentUrl = Attachment,
            AttachmentContentType = "image/jpeg",
        };
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread(ThreadId));
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);
        h.Client.Setup(c => c.GetRecentChatMessagesAsync(MessageSyncHarness.Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, ThreadId, null, CancellationToken.None);
        await using (var db = h.NewDb())
            await h.Service(db).ReconcileRecentAsync(MessageSyncHarness.OrgA, CancellationToken.None);

        // A run that fails: Octorate does not answer for the thread, and the exception reaches the caller.
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, "9002", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));
        await using (var db = h.NewDb())
        {
            await Assert.ThrowsAsync<OctorateServerException>(
                () => h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, "9002", null, CancellationToken.None));
        }

        // The words are in the table (that is the copy) and in no line of the log: not as a message, not as a structured value, not in an exception.
        Assert.Equal(Words, (await h.ReadAsync(db => db.OtaMessages.SingleAsync())).Text);
        Assert.NotEmpty(h.Log.Entries);
        var output = h.Log.AllOutput;
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PROOF TEST: permission rule (OCT-04, AC-NF62-1). A thread of a listing nobody confirmed is stored with no property (unassigned) and counted,
    /// never guessed; it gets its property at the first pass after the mapping is confirmed; an accommodation that is not a listing of the organization,
    /// and one the host rejected, are not read at all (the strict client would fail the test), and another organization has no listing to read.
    /// </summary>
    [Fact]
    public async Task OtaMessageSync_UnconfirmedListing_StoresThreadUnassigned_Test()
    {
        const string Proposed = "700";
        const string Rejected = "701";
        const string Unknown = "999";
        var h = new MessageSyncHarness();
        h.AddListing(Proposed, OctorateMappingStatus.Proposed);
        h.AddListing(Rejected, OctorateMappingStatus.Rejected);
        h.Client.Setup(c => c.GetChatThreadAsync(Proposed, "9100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread("9100", Proposed, "R-9", "142"));
        h.Client.Setup(c => c.GetChatMessagesAsync(Proposed, "9100", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("80001", "9100")]);

        SyncResult stored;
        await using (var db = h.NewDb())
            stored = await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, Proposed, "9100", null, CancellationToken.None);
        Assert.Equal(new SyncResult(1, 1, 0, 1), stored);

        var thread = await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync(t => t.OctorateThreadId == "9100"));
        Assert.Null(thread.PropertyId);
        Assert.Equal(OtaChannel.Booking, thread.ChannelSource);
        var payload = await h.ReadAsync(async db =>
            (await db.OutboxEvents.SingleAsync(e => e.EventType == DomainEventType.GuestMessageReceived)).PayloadJson);
        using (var document = JsonDocument.Parse(payload))
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("propertyId").ValueKind);

        // Not a listing of the organization, rejected, or another organization: nothing is read (no call is set up, a call would throw).
        await using (var db = h.NewDb())
        {
            var service = h.Service(db);
            Assert.Equal(SyncResult.Empty, await service.SyncThreadAsync(MessageSyncHarness.OrgA, Unknown, "9100", null, CancellationToken.None));
            Assert.Equal(SyncResult.Empty, await service.SyncThreadAsync(MessageSyncHarness.OrgA, Rejected, "9100", null, CancellationToken.None));
            Assert.Equal(SyncResult.Empty, await service.SyncThreadAsync(MessageSyncHarness.OrgB, Proposed, "9100", null, CancellationToken.None));
        }

        // A person confirms the mapping to a property of the organization: the next pass gives the thread that property, and counts it assigned.
        var propertyId = Guid.NewGuid();
        h.Confirm(Proposed, propertyId);
        SyncResult assigned;
        await using (var db = h.NewDb())
            assigned = await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, Proposed, "9100", null, CancellationToken.None);

        Assert.Equal(new SyncResult(1, 0, 0, 0), assigned);
        Assert.Equal(propertyId, (await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync(t => t.OctorateThreadId == "9100"))).PropertyId);
        Assert.Equal(1, await h.ReadAsync(db => db.OtaMessages.CountAsync()));

        // A mapping that stops being confirmed takes the property away again: the thread is unassigned, not left under a property that lost its listing.
        h.Mappings.Setup(m => m.ListAsync(MessageSyncHarness.OrgA, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Row(Proposed, OctorateMappingStatus.Proposed, null)]);
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, Proposed, "9100", null, CancellationToken.None);
        Assert.Null((await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync(t => t.OctorateThreadId == "9100"))).PropertyId);
    }

    /// <summary>
    /// PROOF TEST: trust rule (ADR-023-R6, AC1 of T02). A listing whose license has no chat module is kept as the state <c>chat_not_licensed</c>
    /// on its checkpoints, the other listings of the organization are read all the same and the pass does not fail, and the listing is not asked
    /// again for an hour. Nothing is stored for it, and it is not an empty inbox: its state says why.
    /// </summary>
    [Fact]
    public async Task OtaMessageSync_ChatNotLicensed_IsKeptAsTheStateOfTheListing_Test()
    {
        const string Unlicensed = "700";
        var h = new MessageSyncHarness();
        h.AddListing(Unlicensed, OctorateMappingStatus.Confirmed);
        h.Client.Setup(c => c.GetRecentChatMessagesAsync(Unlicensed, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ChatNotLicensed(403));
        h.Client.Setup(c => c.GetRecentChatMessagesAsync(MessageSyncHarness.Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread(ThreadId));
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);

        SyncResult result;
        await using (var db = h.NewDb())
            result = await h.Service(db).ReconcileRecentAsync(MessageSyncHarness.OrgA, CancellationToken.None);

        Assert.Equal(new SyncResult(1, 1, 0, 0), result);
        var states = await h.ReadAsync(db => db.OtaMessageSyncStates.ToListAsync());
        var broken = Assert.Single(states, s => s.OctorateAccommodationId == Unlicensed);
        Assert.Equal("chat_not_licensed", broken.LastError);
        Assert.Null(broken.LastRecentPassAt);
        var working = Assert.Single(states, s => s.OctorateAccommodationId == MessageSyncHarness.Accommodation);
        Assert.Null(working.LastError);
        Assert.NotNull(working.LastRecentPassAt);
        Assert.Equal(h.ConnectionId, working.ConnectionId);
        Assert.Empty(await h.ReadAsync(db => db.OtaMessageThreads.Where(t => t.OctorateAccommodationId == Unlicensed).ToListAsync()));

        // The next pass, a few minutes later: the listing rests, the other one is read again.
        await using (var db = h.NewDb())
            await h.Service(db).ReconcileRecentAsync(MessageSyncHarness.OrgA, CancellationToken.None);
        h.Client.Verify(c => c.GetRecentChatMessagesAsync(Unlicensed, It.IsAny<CancellationToken>()), Times.Once);
        h.Client.Verify(c => c.GetRecentChatMessagesAsync(MessageSyncHarness.Accommodation, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// The import of a property (AC-NF62-1): it reads the pages of the threads of the confirmed listing, ends with a page that has nothing, keeps
    /// the end on the checkpoints of the listing, and running it again adds nothing. A thread with a reservation is a Reservation, one without is a
    /// Request whose channel comes from the connection it comes through.
    /// </summary>
    [Fact]
    public async Task OtaMessageSync_ImportProperty_ReadsEveryPageAndNeverDuplicates()
    {
        var h = new MessageSyncHarness();
        var acc = MessageSyncHarness.Accommodation;
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([MessageSyncHarness.Thread("1"), MessageSyncHarness.Thread("2", reservationId: "R-2")], 2));
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([MessageSyncHarness.Thread("3", reservationId: null)], 1));
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([], 0));
        foreach (var id in new[] { "1", "2", "3" })
        {
            var threadId = id;
            h.Client.Setup(c => c.GetChatThreadAsync(acc, threadId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(threadId == "3"
                    ? MessageSyncHarness.Thread(threadId, reservationId: null) with { ConnectionIds = ["55"] }
                    : MessageSyncHarness.Thread(threadId, reservationId: $"R-{threadId}"));
            h.Client.Setup(c => c.GetChatMessagesAsync(acc, threadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([MessageSyncHarness.Inbound($"7{threadId}", threadId)]);
        }

        h.Client.Setup(c => c.GetConnectionsAsync(acc, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateChannelConnection("55", "142", "Booking.com", "123", "CONNECTED")]);

        SyncResult first;
        SyncResult again;
        await using (var db = h.NewDb())
            first = await h.Service(db).ImportPropertyAsync(h.PropertyId, CancellationToken.None);
        await using (var db = h.NewDb())
            again = await h.Service(db).ImportPropertyAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(new SyncResult(3, 3, 0, 0), first);
        Assert.Equal(new SyncResult(3, 0, 0, 0), again);
        var (threads, messages, state) = await h.ReadAsync(async db =>
            (await db.OtaMessageThreads.OrderBy(t => t.OctorateThreadId).ToListAsync(), await db.OtaMessages.CountAsync(), await db.OtaMessageSyncStates.SingleAsync()));
        Assert.Equal(3, messages);
        Assert.Equal(["1", "2", "3"], threads.Select(t => t.OctorateThreadId));
        Assert.Equal([OtaThreadKind.Reservation, OtaThreadKind.Reservation, OtaThreadKind.Request], threads.Select(t => t.Kind));
        Assert.Equal(OtaChannel.Booking, threads[2].ChannelSource);
        Assert.NotNull(state.InitialImportCompletedAt);
        Assert.Null(state.ImportCursor);
        Assert.Null(state.LastError);

        // A property that is not in the organization's mapping reads nothing (the strict client would fail the test if it did).
        await using var other = h.NewDb();
        Assert.Equal(SyncResult.Empty, await h.Service(other).ImportPropertyAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>An import that was interrupted resumes at the position it saved: the pages before it are not read again, and the end is recorded.</summary>
    [Fact]
    public async Task OtaMessageSync_ImportProperty_ResumesAtTheSavedPosition()
    {
        var h = new MessageSyncHarness();
        var acc = MessageSyncHarness.Accommodation;
        await using (var seed = h.NewDb())
        {
            seed.OtaMessageSyncStates.Add(new OtaMessageSyncState
            {
                OrgId = MessageSyncHarness.OrgA,
                OctorateAccommodationId = acc,
                ConnectionId = h.ConnectionId,
                PropertyId = h.PropertyId,
                ImportCursor = "2",
                UpdatedAt = MessageSyncHarness.Now.UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        // Only the page at 2 and the end are set up: a read of the page at 0 would fail the test.
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([MessageSyncHarness.Thread("3")], 1));
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([], 0));
        h.Client.Setup(c => c.GetChatThreadAsync(acc, "3", It.IsAny<CancellationToken>())).ReturnsAsync(MessageSyncHarness.Thread("3"));
        h.Client.Setup(c => c.GetChatMessagesAsync(acc, "3", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("73", "3")]);

        SyncResult result;
        await using (var db = h.NewDb())
            result = await h.Service(db).ImportPropertyAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(new SyncResult(1, 1, 0, 0), result);
        var state = await h.ReadAsync(db => db.OtaMessageSyncStates.SingleAsync());
        Assert.NotNull(state.InitialImportCompletedAt);
        Assert.Null(state.ImportCursor);
    }

    /// <summary>
    /// A run that is out of budget stops before it reads anything more and reports itself not complete; the next run, with time, goes to the end and
    /// records it.
    /// </summary>
    [Fact]
    public async Task OtaMessageSync_ImportProperty_OutOfBudget_StopsAndSaysItIsNotComplete()
    {
        var h = new MessageSyncHarness();
        var acc = MessageSyncHarness.Accommodation;
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([MessageSyncHarness.Thread("1")], 1));
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([], 0));
        h.Client.Setup(c => c.GetChatThreadAsync(acc, "1", It.IsAny<CancellationToken>())).ReturnsAsync(MessageSyncHarness.Thread("1"));
        h.Client.Setup(c => c.GetChatMessagesAsync(acc, "1", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("71", "1")]);

        // No time at all: the run starts, finds its budget spent and reads nothing.
        SyncResult stopped;
        await using (var db = h.NewDb())
            stopped = await h.Service(db).ImportPropertyAsync(h.PropertyId, CancellationToken.None, TimeSpan.Zero);
        Assert.False(stopped.Complete);
        Assert.Equal(0, await h.ReadAsync(db => db.OtaMessageThreads.CountAsync()));
        Assert.Empty(await h.ReadAsync(db => db.OtaMessageSyncStates.ToListAsync()));

        // The next run, with time, goes to the end.
        SyncResult done;
        await using (var db = h.NewDb())
            done = await h.Service(db).ImportPropertyAsync(h.PropertyId, CancellationToken.None, TimeSpan.FromMinutes(4));
        Assert.True(done.Complete);
        Assert.Equal(new SyncResult(1, 1, 0, 0), done);
        Assert.NotNull((await h.ReadAsync(db => db.OtaMessageSyncStates.SingleAsync())).InitialImportCompletedAt);
    }
}
