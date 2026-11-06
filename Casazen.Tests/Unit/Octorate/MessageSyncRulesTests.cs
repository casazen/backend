using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The rules of the sync of the OTA conversations that the proofs do not reach (E05/F01/US01/T02): the deep pass reads the threads with activity in its
/// window and looks back over the window; a thread is linked to its booking, also a quiet one, when the booking appears; and an outbound row CasaZen
/// could not confirm is completed by the message of Octorate, never duplicated. The client of Octorate is a strict stub; the database is in memory.
/// </summary>
public class MessageSyncRulesTests
{
    private const string ThreadId = "9001";

    [Fact]
    public async Task DeepPass_ReadsTheThreadsWithActivityInItsWindow_LooksBackOverTheWindow_AndSavesItsPosition()
    {
        var h = new MessageSyncHarness();
        var acc = MessageSyncHarness.Accommodation;
        var now = MessageSyncHarness.Now.UtcDateTime;
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage(
            [
                MessageSyncHarness.Thread("1") with { LastActivityAt = now.AddDays(-1) },
                MessageSyncHarness.Thread("2") with { LastActivityAt = now.AddDays(-10) },
            ], 2));
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([MessageSyncHarness.Thread("3") with { LastActivityAt = now.AddDays(-20) }], 1));
        h.Client.Setup(c => c.GetChatThreadAsync(acc, "1", It.IsAny<CancellationToken>())).ReturnsAsync(MessageSyncHarness.Thread("1"));
        h.Client.Setup(c => c.GetChatMessagesAsync(acc, "1", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("71", "1")]);

        SyncResult first;
        await using (var db = h.NewDb())
            first = await h.Service(db).ReconcileDeepAsync(MessageSyncHarness.OrgA, TimeSpan.FromDays(2), CancellationToken.None);

        // Only the thread with activity in the two days before the pass is read; the others are older, and the list is newest first, so it ends there.
        Assert.Equal(new SyncResult(1, 1, 0, 0), first);
        Assert.Equal(["1"], (await h.ReadAsync(db => db.OtaMessageThreads.ToListAsync())).Select(t => t.OctorateThreadId));
        h.Client.Verify(c => c.GetChatMessagesAsync(acc, "1", null, It.IsAny<CancellationToken>()), Times.Once);
        var state = await h.ReadAsync(db => db.OtaMessageSyncStates.SingleAsync());
        Assert.Equal(now, state.LastDeepPassAt);
        Assert.Null(state.LastError);

        // The next pass looks back over the window from the last one: the messages of the thread are read from two days before it, not from the newest
        // message that is stored, so a message older than that one that nobody delivered is found.
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([MessageSyncHarness.Thread("1") with { LastActivityAt = now.AddHours(-1) }], 1));
        h.Client.Setup(c => c.GetChatThreadsAsync(acc, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatThreadPage([], 0));
        await using (var db = h.NewDb())
            await h.Service(db).ReconcileDeepAsync(MessageSyncHarness.OrgA, TimeSpan.FromDays(2), CancellationToken.None);

        h.Client.Verify(
            c => c.GetChatMessagesAsync(acc, "1", It.Is<DateTime?>(d => d == now.AddDays(-2)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ARequest_IsLinkedToItsBooking_WhenTheReservationAppears_AndAQuietThreadWhenTheBookingAppears()
    {
        var h = new MessageSyncHarness();
        var acc = MessageSyncHarness.Accommodation;
        h.Client.Setup(c => c.GetChatMessagesAsync(acc, "9200", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("72", "9200")]);
        h.Client.Setup(c => c.GetConnectionsAsync(acc, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateChannelConnection("55", "142", "Booking.com", "123", "CONNECTED")]);

        // An inquiry: no reservation yet, the channel comes from the connection it comes through.
        h.Client.Setup(c => c.GetChatThreadAsync(acc, "9200", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread("9200", reservationId: null) with { ConnectionIds = ["55"] });
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, acc, "9200", null, CancellationToken.None);

        var inquiry = await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync());
        Assert.Equal(OtaThreadKind.Request, inquiry.Kind);
        Assert.Equal(OtaChannel.Booking, inquiry.ChannelSource);
        Assert.Null(inquiry.BookingId);
        Assert.Null(inquiry.OctorateReservationId);

        // The guest books: the thread carries the reservation, which CasaZen does not hold as a booking yet.
        h.Client.Setup(c => c.GetChatThreadAsync(acc, "9200", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread("9200", reservationId: "R-9200", channelId: "142"));
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, acc, "9200", null, CancellationToken.None);

        var booked = await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync());
        Assert.Equal(OtaThreadKind.Reservation, booked.Kind);
        Assert.Equal("R-9200", booked.OctorateReservationId);
        Assert.Null(booked.BookingId);

        // The booking arrives from the channel manager. The thread is quiet: the next pass links it without reading it again.
        var guest = new Guest { OrgId = MessageSyncHarness.OrgA, FirstName = "Giulietta", LastName = "Verdi", Email = "giulietta@example.com" };
        var booking = new Booking
        {
            OrgId = MessageSyncHarness.OrgA,
            PropertyId = h.PropertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 13, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.BookingCom,
            OctorateReservationId = "R-9200",
        };
        await using (var seed = h.NewDb())
        {
            seed.Guests.Add(guest);
            seed.Bookings.Add(booking);
            await seed.SaveChangesAsync();
        }

        h.Client.Setup(c => c.GetRecentChatMessagesAsync(acc, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await using (var db = h.NewDb())
            await h.Service(db).ReconcileRecentAsync(MessageSyncHarness.OrgA, CancellationToken.None);

        var linked = await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync());
        Assert.Equal(booking.Id, linked.BookingId);
        Assert.Equal(OtaThreadKind.Reservation, linked.Kind);
        h.Client.Verify(c => c.GetChatThreadAsync(acc, "9200", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ARowCasaZenCouldNotConfirm_IsCompletedByTheMessageOfOctorate_AndNeverDuplicated()
    {
        var h = new MessageSyncHarness();
        var acc = MessageSyncHarness.Accommodation;
        var now = MessageSyncHarness.Now.UtcDateTime;
        h.Client.Setup(c => c.GetChatThreadAsync(acc, ThreadId, It.IsAny<CancellationToken>())).ReturnsAsync(MessageSyncHarness.Thread(ThreadId));
        h.Client.Setup(c => c.GetChatMessagesAsync(acc, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", ThreadId)]);
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, acc, ThreadId, null, CancellationToken.None);

        // CasaZen wrote two messages and never got the answer of Octorate for them: they wait for a person to verify, with no Octorate id.
        var threadGuid = (await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync())).Id;
        await using (var seed = h.NewDb())
        {
            seed.OtaMessages.AddRange(
                Pending(threadGuid, "Benvenuti, il codice della cassetta e 1234", now.AddMinutes(-7), "k-1"),
                Pending(threadGuid, "Il parcheggio e gratuito", now.AddMinutes(-40), "k-2"));
            await seed.SaveChangesAsync();
        }

        // Octorate has both: the first was sent a minute after CasaZen wrote it, the second thirty-eight minutes after. The same text within ten
        // minutes is the same message; the same text much later is not.
        h.Client.Setup(c => c.GetChatMessagesAsync(acc, ThreadId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                MessageSyncHarness.Inbound("70001", ThreadId),
                Outbound("80001", "Benvenuti, il codice della cassetta e 1234", now.AddMinutes(-6)),
                Outbound("80002", "Il parcheggio e gratuito", now.AddMinutes(-2)),
            ]);
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, acc, ThreadId, null, CancellationToken.None);

        var rows = await h.ReadAsync(db => db.OtaMessages.OrderBy(m => m.SentAt).ToListAsync());
        Assert.Equal(4, rows.Count);

        // The first outbound row got the id and the state Octorate reports, and kept what CasaZen knew of it: its key, its author, its provenance.
        var completed = Assert.Single(rows, m => m.IdempotencyKey == "k-1");
        Assert.Equal("80001", completed.OctorateMessageId);
        Assert.Equal(OtaMessageDeliveryState.Sent, completed.DeliveryState);
        Assert.Equal("auth0|host", completed.SentByUserId);
        Assert.Equal(MessageOrigin.Person, completed.Origin);

        // The second is still waiting for a person (40 minutes before its message), and the message of Octorate is a row of its own.
        var waiting = Assert.Single(rows, m => m.IdempotencyKey == "k-2");
        Assert.Null(waiting.OctorateMessageId);
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, waiting.DeliveryState);
        var separate = Assert.Single(rows, m => m.OctorateMessageId == "80002");
        Assert.Equal((OtaMessageDirection.Outbound, MessageOrigin.Person, (string?)null), (separate.Direction, separate.Origin, separate.IdempotencyKey));
    }

    private static OtaMessage Pending(Guid threadId, string text, DateTime sentAt, string key) => new()
    {
        OrgId = MessageSyncHarness.OrgA,
        ThreadId = threadId,
        OctorateMessageId = null,
        Direction = OtaMessageDirection.Outbound,
        SenderName = "Casa del Lago",
        Text = text,
        SentAt = sentAt,
        DeliveryState = OtaMessageDeliveryState.ManualVerify,
        IdempotencyKey = key,
        Origin = MessageOrigin.Person,
        SentByUserId = "auth0|host",
        CreatedAt = sentAt,
    };

    private static OctorateChatMessage Outbound(string id, string text, DateTime sentAt) => new()
    {
        Id = id,
        ThreadId = ThreadId,
        Processor = "CHANNEL",
        Direction = OtaMessageDirection.Outbound,
        SenderName = "Marco Host",
        Text = text,
        SentAt = sentAt,
        DeliveryState = OtaMessageDeliveryState.Sent,
    };
}
