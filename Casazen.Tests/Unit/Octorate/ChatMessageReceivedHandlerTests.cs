using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The handler of the chat notification of the webhook (E05/F01/US01/T03, ADR-032, ADR-022-R7, OCT-08): it reacts to <c>CHAT_MESSAGE_RECEIVED</c>
/// only, re-reads the thread through the API before any effect and never trusts the body alone, stores a message once however many times the
/// notification arrives, and fails (so the outbox delivers again) only where a new attempt can help. The client of Octorate is a strict stub; the
/// database is in memory.
/// </summary>
public class ChatMessageReceivedHandlerTests
{
    private const string ThreadId = "9001";

    /// <summary>
    /// PROOF TEST: data rule (ADR-022-R7, AC-NF62-2). The same notification delivered twice (Octorate retries for two days and folds nothing it
    /// has not seen), each time to a handler of its own scope as the event bus builds it, makes one message and one event; the thread is read
    /// again each time, because the body is a signal and never the data.
    /// </summary>
    [Fact]
    public async Task ChatMessageReceived_SameEventTwice_StoresOneMessage_Test()
    {
        var h = new MessageSyncHarness();
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread(ThreadId));
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);
        var notification = Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, ThreadId);

        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(notification, CancellationToken.None);
        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(notification, CancellationToken.None);

        var (threads, messages, received) = await h.ReadAsync(async db => (
            await db.OtaMessageThreads.CountAsync(),
            await db.OtaMessages.CountAsync(),
            await db.OutboxEvents.CountAsync(e => e.EventType == DomainEventType.GuestMessageReceived)));
        Assert.Equal((1, 1, 1), (threads, messages, received));
        h.Client.Verify(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()), Times.Exactly(2));
        h.Client.Verify(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ChatMessageReceived_OtherKindsOfNotificationAndTheFlagOff_DoNothing()
    {
        var h = new MessageSyncHarness();

        // A notification of a reservation is the booking handlers': nothing is read (the strict client would fail the test).
        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ReservationChange, MessageSyncHarness.Accommodation, ThreadId), CancellationToken.None);

        // The connector is off: no call is made even for a chat notification.
        h.Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(false);
        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, ThreadId), CancellationToken.None);

        Assert.Equal(0, await h.ReadAsync(db => db.OtaMessageThreads.CountAsync()));
        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChatMessageReceived_ANotificationThatNamesNoUsableThread_MakesTheRecentPassReadTheListings()
    {
        var h = new MessageSyncHarness();
        h.Client.Setup(c => c.GetRecentChatMessagesAsync(MessageSyncHarness.Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread(ThreadId));
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);

        // No thread in the body, and a thread that is not a number: both fall back to what the last 24 hours say.
        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, null), CancellationToken.None);
        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, "abc-1"), CancellationToken.None);

        Assert.Equal(1, await h.ReadAsync(db => db.OtaMessages.CountAsync()));
        h.Client.Verify(c => c.GetRecentChatMessagesAsync(MessageSyncHarness.Accommodation, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ChatMessageReceived_AThreadOctorateDoesNotKnow_MakesTheRecentPassReadTheListings()
    {
        var h = new MessageSyncHarness();
        var acc = MessageSyncHarness.Accommodation;

        // The body names 5555, which is not a thread of the chat (it may be another object id): Octorate answers 404, and the notification is
        // still a signal that something was written, so the recent pass finds the thread that had news.
        h.Client.Setup(c => c.GetChatThreadAsync(acc, "5555", It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateNotFoundException());
        h.Client.Setup(c => c.GetRecentChatMessagesAsync(acc, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);
        h.Client.Setup(c => c.GetChatThreadAsync(acc, ThreadId, It.IsAny<CancellationToken>())).ReturnsAsync(MessageSyncHarness.Thread(ThreadId));
        h.Client.Setup(c => c.GetChatMessagesAsync(acc, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);

        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, acc, "5555"), CancellationToken.None);

        Assert.Equal(["70001"], (await h.ReadAsync(db => db.OtaMessages.ToListAsync())).Select(m => m.OctorateMessageId));
        h.Client.Verify(c => c.GetRecentChatMessagesAsync(acc, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChatMessageReceived_AnAccommodationOfAnotherOrg_ReadsNothing()
    {
        var h = new MessageSyncHarness();

        // The body names an accommodation the org does not have in its mapping: it is not read, whatever the notification says.
        await using var db = h.NewDb();
        await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, "999", ThreadId), CancellationToken.None);

        h.Client.VerifyNoOtherCalls();
        Assert.Equal(0, await h.ReadAsync(d => d.OtaMessageThreads.CountAsync()));
    }

    [Fact]
    public async Task ChatMessageReceived_FailsOnlyWhereANewAttemptCanHelp()
    {
        var h = new MessageSyncHarness();

        // An error of Octorate: the event is delivered again, and the code is on the checkpoints of the listing.
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        await using (var db = h.NewDb())
        {
            await Assert.ThrowsAsync<OctorateServerException>(
                () => Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, ThreadId), CancellationToken.None));
        }

        Assert.Equal("octorate_unavailable", (await h.ReadAsync(db => db.OtaMessageSyncStates.SingleAsync())).LastError);

        // A rate limit that did not clear: the same.
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429));
        await using (var db = h.NewDb())
        {
            await Assert.ThrowsAsync<OctorateRateLimitException>(
                () => Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, ThreadId), CancellationToken.None));
        }

        // An org that disconnected, and a listing whose license has no chat module: a new attempt would find the same, so the handler returns.
        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.NotConnected());
        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, ThreadId), CancellationToken.None);

        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, ThreadId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ChatNotLicensed(403));
        await using (var db = h.NewDb())
            await Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, ThreadId), CancellationToken.None);

        Assert.Equal("chat_not_licensed", (await h.ReadAsync(db => db.OtaMessageSyncStates.SingleAsync())).LastError);
        Assert.Equal(0, await h.ReadAsync(db => db.OtaMessageThreads.CountAsync()));
    }

    /// <summary>
    /// The handler runs in the outbox dispatcher, one event after the other for every org within a budget of seconds, so it never waits for the calls of
    /// the org to Octorate to refill: with the bucket down to the calls kept for the people who use CasaZen it ends at once with the rate limit (the
    /// dispatcher delivers the event again at its next run) and nothing is read, also when the notification names no thread and the recent pass would
    /// read. A service that waited would wait for a bucket that does not refill here (the clock is fixed): it ends with the rate limit only after the
    /// two minutes it may wait, and the test, which allows it twenty seconds, fails.
    /// </summary>
    [Fact]
    public async Task ChatMessageReceived_WithTheCallsOfTheOrgUsedUp_EndsAtOnceWithTheRateLimit_AndDoesNotWait()
    {
        var h = new MessageSyncHarness();
        while (h.Limiter.AvailableCalls(MessageSyncHarness.OrgA) > MessageSyncService.RateReserve)
            await h.Limiter.TryAcquireAsync(MessageSyncHarness.OrgA);

        foreach (var objectId in new[] { ThreadId, null })
        {
            await using var db = h.NewDb();
            var handling = Handler(h, db).HandleAsync(Notification(OctorateWebhookEvents.ChatMessageReceived, MessageSyncHarness.Accommodation, objectId), CancellationToken.None);
            await Assert.ThrowsAsync<OctorateRateLimitException>(() => handling.WaitAsync(TimeSpan.FromSeconds(20)));
        }

        h.Client.VerifyNoOtherCalls();
        Assert.Equal(0, await h.ReadAsync(db => db.OtaMessageThreads.CountAsync()));
        Assert.Empty(await h.ReadAsync(db => db.OtaMessageSyncStates.ToListAsync()));
    }

    /// <summary>The handler as the container builds it for one delivery: it and the service of its scope share the pacing of the scope.</summary>
    private static ChatMessageReceivedHandler Handler(MessageSyncHarness h, AppDbContext db)
    {
        var pacing = new MessageSyncPacing();
        return new ChatMessageReceivedHandler(
            h.Service(db, pacing),
            new OctorateOrgScope(NullTenantContext.Instance),
            h.Flags.Object,
            pacing,
            NullLogger<ChatMessageReceivedHandler>.Instance);
    }

    /// <summary>
    /// A notification of the webhook as the event bus hands it to a handler: the same event id on every delivery, the payload of the ids the controller
    /// kept.
    /// </summary>
    private static OctorateWebhookReceivedEvent Notification(string type, string accommodationId, string? objectId) => new(
        Guid.Parse("a1b2c3d4-0000-4000-8000-00000000e001"),
        MessageSyncHarness.OrgA,
        DomainEventType.OctorateWebhookReceived,
        Guid.NewGuid(),
        MessageSyncHarness.Now.UtcDateTime,
        JsonSerializer.Serialize(new OctorateWebhookPayload(type, accommodationId, objectId, MessageSyncHarness.Now.UtcDateTime)));
}
