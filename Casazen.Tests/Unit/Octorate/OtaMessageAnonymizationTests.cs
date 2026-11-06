using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The copy of the OTA conversations leaves with the guest (E05/F01/US01/T03, ADR-032-R13, docs/runbooks/gdpr.md): the erasure or the anonymization
/// of a guest empties the words and the names of the threads of its bookings, a later read of the channel manager brings none of it back, and the
/// retention anonymizes the copy when the data of its stay expires, and only then.
/// </summary>
public class OtaMessageAnonymizationTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// PROOF TEST: privacy rule (ADR-032-R13). After the erasure of a guest the text and the names of its OTA conversations are gone (the words, the
    /// name of the guest, the name of the sender, the link to an attachment), the thread is marked, and what shows that a conversation took place
    /// (the dates, the direction, the state, the ids) stays; the conversations of another guest and the inquiry with no booking are not touched; a
    /// second erasure changes nothing.
    /// </summary>
    [Fact]
    public async Task GuestErasure_AnonymizesOtaMessages_Test()
    {
        await using var db = NewDb($"ota-erasure-{Guid.NewGuid():N}");
        var orgId = Guid.NewGuid();
        var erased = new Guest { OrgId = orgId, FirstName = "Giulietta", LastName = "Verdi", Email = "giulietta@example.com" };
        var other = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Neri", Email = "anna@example.com" };
        var propertyId = Guid.NewGuid();
        var erasedBooking = NewBooking(orgId, propertyId, erased.Id, "R-1");
        var otherBooking = NewBooking(orgId, propertyId, other.Id, "R-2");
        var mine = NewThread(orgId, erasedBooking.Id, "1", "Giulietta Verdi");
        var theirs = NewThread(orgId, otherBooking.Id, "2", "Anna Neri");
        var inquiry = NewThread(orgId, null, "3", "Un Altro Ospite");
        db.Guests.AddRange(erased, other);
        db.Bookings.AddRange(erasedBooking, otherBooking);
        db.OtaMessageThreads.AddRange(mine, theirs, inquiry);
        db.OtaMessages.AddRange(
            NewMessage(mine, "10", OtaMessageDirection.Inbound, "Giulietta Verdi", "Il mio telefono e 333 1234567", "https://resx.example.test/doc-1.jpg"),
            NewMessage(mine, "11", OtaMessageDirection.Outbound, "Casa del Lago", "Grazie, ci vediamo sabato", null),
            NewMessage(theirs, "20", OtaMessageDirection.Inbound, "Anna Neri", "Siamo in due", null),
            NewMessage(inquiry, "30", OtaMessageDirection.Inbound, "Un Altro Ospite", "Avete posto a Pasqua?", null));
        await db.SaveChangesAsync();
        var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);

        var outcome = await eraser.AnonymizeAsync(erased, Now, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(outcome.Changed);
        db.ChangeTracker.Clear();
        var erasedThread = await db.OtaMessageThreads.SingleAsync(t => t.Id == mine.Id);
        Assert.Equal(GuestDataEraser.AnonymizedName, erasedThread.GuestDisplayName);
        Assert.Equal(Now, erasedThread.AnonymizedAt);
        var erasedMessages = await db.OtaMessages.Where(m => m.ThreadId == mine.Id).OrderBy(m => m.OctorateMessageId).ToListAsync();
        Assert.Equal(2, erasedMessages.Count);
        Assert.All(erasedMessages, message =>
        {
            Assert.Equal(string.Empty, message.Text);
            Assert.Equal(GuestDataEraser.AnonymizedName, message.SenderName);
            Assert.Null(message.AttachmentUrl);
            Assert.Null(message.AttachmentContentType);
        });

        // The shape of the conversation stays: who spoke, when, the state, the ids.
        Assert.Equal(["10", "11"], erasedMessages.Select(m => m.OctorateMessageId));
        Assert.Equal([OtaMessageDirection.Inbound, OtaMessageDirection.Outbound], erasedMessages.Select(m => m.Direction));
        Assert.All(erasedMessages, message => Assert.Equal(Now.AddHours(-3), message.SentAt));

        // Nobody else's words are touched: the other guest's conversation and the inquiry have no part in this erasure.
        var theirMessage = await db.OtaMessages.SingleAsync(m => m.ThreadId == theirs.Id);
        Assert.Equal(("Siamo in due", "Anna Neri"), (theirMessage.Text, theirMessage.SenderName));
        Assert.Null((await db.OtaMessageThreads.SingleAsync(t => t.Id == theirs.Id)).AnonymizedAt);
        var inquiryMessage = await db.OtaMessages.SingleAsync(m => m.ThreadId == inquiry.Id);
        Assert.Equal("Avete posto a Pasqua?", inquiryMessage.Text);
        Assert.Equal("Un Altro Ospite", (await db.OtaMessageThreads.SingleAsync(t => t.Id == inquiry.Id)).GuestDisplayName);

        // Idempotent: nothing is left to remove.
        var again = await eraser.AnonymizeAsync(await db.Guests.SingleAsync(g => g.Id == erased.Id), Now.AddDays(1), CancellationToken.None);
        Assert.False(again.Changed);
        Assert.Equal(Now, (await db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.Id == mine.Id)).AnonymizedAt);
    }

    /// <summary>
    /// The next read of the channel manager brings none of the erased words back: a message that arrives in the conversation of an erased guest is
    /// stored without its words and its name, and the thread keeps the name it was given.
    /// </summary>
    [Fact]
    public async Task AnonymizedThread_GetsNoWordsBackFromTheNextRead()
    {
        var h = new MessageSyncHarness();
        var guest = new Guest { OrgId = MessageSyncHarness.OrgA, FirstName = "Giulietta", LastName = "Verdi", Email = "giulietta@example.com" };
        await using (var seed = h.NewDb())
        {
            seed.Guests.Add(guest);
            seed.Bookings.Add(NewBooking(MessageSyncHarness.OrgA, h.PropertyId, guest.Id, "R-4711"));
            await seed.SaveChangesAsync();
        }

        h.Client.Setup(c => c.GetChatThreadAsync(MessageSyncHarness.Accommodation, "9001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageSyncHarness.Thread("9001"));
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, "9001", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MessageSyncHarness.Inbound("70001", "9001", "Prima dell'erasure")]);
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, "9001", null, CancellationToken.None);

        var linked = await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync());
        Assert.NotNull(linked.BookingId);
        Assert.Equal("Giulietta Verdi", linked.GuestDisplayName);

        await using (var db = h.NewDb())
        {
            var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);
            await eraser.AnonymizeAsync(await db.Guests.SingleAsync(), Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        // A new message of the guest, read after the erasure.
        h.Client.Setup(c => c.GetChatMessagesAsync(MessageSyncHarness.Accommodation, "9001", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                MessageSyncHarness.Inbound("70001", "9001", "Prima dell'erasure"),
                MessageSyncHarness.Inbound("70002", "9001", "Dopo l'erasure, scrivetemi a giulietta@example.com") with
                {
                    AttachmentUrl = "https://resx.example.test/doc-2.jpg",
                    AttachmentContentType = "image/jpeg",
                },
            ]);
        await using (var db = h.NewDb())
            await h.Service(db).SyncThreadAsync(MessageSyncHarness.OrgA, MessageSyncHarness.Accommodation, "9001", null, CancellationToken.None);

        var thread = await h.ReadAsync(db => db.OtaMessageThreads.SingleAsync());
        var messages = await h.ReadAsync(db => db.OtaMessages.OrderBy(m => m.OctorateMessageId).ToListAsync());
        Assert.Equal(GuestDataEraser.AnonymizedName, thread.GuestDisplayName);
        Assert.NotNull(thread.AnonymizedAt);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.Equal(string.Empty, message.Text);
            Assert.Equal(GuestDataEraser.AnonymizedName, message.SenderName);
            Assert.Null(message.AttachmentUrl);
        });
    }

    /// <summary>
    /// The retention (ADR-032-R13): the copy is anonymized when the period of the guest data of its stay has ended (counted from the check-out), a
    /// stay that is still open is left alone, an inquiry with no booking is counted from its last message, nothing is anonymized without a period and
    /// its source, and a second run changes nothing.
    /// </summary>
    [Fact]
    public async Task Retention_AnonymizesTheCopyWhenTheStayDataExpires_AndOnlyThen()
    {
        await using var db = NewDb($"ota-retention-{Guid.NewGuid():N}");
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Giulietta", LastName = "Verdi", Email = "giulietta@example.com" };
        var old = NewBooking(orgId, propertyId, guest.Id, "R-1", new DateTime(2020, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        var recent = NewBooking(orgId, propertyId, guest.Id, "R-2", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var open = NewBooking(orgId, propertyId, guest.Id, "R-3", new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc), BookingStatus.Confirmed);
        db.Guests.Add(guest);
        db.Bookings.AddRange(old, recent, open);
        var due = NewThread(orgId, old.Id, "1", "Ospite Uno");
        var notYet = NewThread(orgId, recent.Id, "2", "Ospite Due");
        var stillOpen = NewThread(orgId, open.Id, "3", "Ospite Tre");
        var oldInquiry = NewThread(orgId, null, "4", "Ospite Quattro", lastMessageAt: new DateTime(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var newInquiry = NewThread(orgId, null, "5", "Ospite Cinque", lastMessageAt: new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        db.OtaMessageThreads.AddRange(due, notYet, stillOpen, oldInquiry, newInquiry);
        foreach (var thread in new[] { due, notYet, stillOpen, oldInquiry, newInquiry })
            db.OtaMessages.Add(NewMessage(thread, $"m{thread.OctorateThreadId}", OtaMessageDirection.Inbound, thread.GuestDisplayName!, "Parole di " + thread.GuestDisplayName, null));
        await db.SaveChangesAsync();

        var clock = new FixedTimeProvider(new DateTimeOffset(Now));
        var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);

        // Without a period and its source nothing is anonymized.
        var notConfigured = new OtaMessageRetentionService(
            db, eraser, Microsoft.Extensions.Options.Options.Create(new GdprOptions()), clock, NullLogger<OtaMessageRetentionService>.Instance);
        var idle = await notConfigured.ApplyRetentionAsync();
        Assert.Equal(new OtaMessageRetentionResult(false, 0, 0), idle);
        Assert.Equal(0, await db.OtaMessageThreads.CountAsync(t => t.AnonymizedAt != null));

        var options = new GdprOptions
        {
            Retention = new GdprRetentionOptions
            {
                AlloggiatiData = new RetentionPeriodOptions { Years = 5, Source = "decision of the product owner" },
            },
        };
        var service = new OtaMessageRetentionService(
            db, eraser, Microsoft.Extensions.Options.Options.Create(options), clock, NullLogger<OtaMessageRetentionService>.Instance);

        var first = await service.ApplyRetentionAsync();

        // The stay checked out in 2020 and the inquiry of 2020 are past five years; the stay of last month, the open stay and the new inquiry are not.
        Assert.Equal(new OtaMessageRetentionResult(true, 2, 2), first);
        db.ChangeTracker.Clear();
        var anonymized = await db.OtaMessageThreads.Where(t => t.AnonymizedAt != null).Select(t => t.OctorateThreadId).OrderBy(id => id).ToListAsync();
        Assert.Equal(["1", "4"], anonymized);
        Assert.All(
            await db.OtaMessages.Where(m => m.ThreadId == due.Id || m.ThreadId == oldInquiry.Id).ToListAsync(),
            message => Assert.Equal((string.Empty, GuestDataEraser.AnonymizedName), (message.Text, message.SenderName)));
        Assert.All(
            await db.OtaMessages.Where(m => m.ThreadId == notYet.Id || m.ThreadId == stillOpen.Id || m.ThreadId == newInquiry.Id).ToListAsync(),
            message => Assert.StartsWith("Parole di", message.Text, StringComparison.Ordinal));

        // A second run the same night changes nothing.
        var second = await service.ApplyRetentionAsync();
        Assert.Equal(new OtaMessageRetentionResult(true, 0, 0), second);
    }

    private static AppDbContext NewDb(string databaseName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, NullTenantContext.Instance);

    private static Booking NewBooking(
        Guid orgId,
        Guid propertyId,
        Guid guestId,
        string reservationId,
        DateTime? checkOut = null,
        BookingStatus status = BookingStatus.CheckedOut)
    {
        return new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = (checkOut ?? Now.AddDays(-5)).AddDays(-3),
            CheckOutDate = checkOut ?? Now.AddDays(-5),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = BookingSource.Airbnb,
            OctorateReservationId = reservationId,
        };
    }

    private static OtaMessageThread NewThread(Guid orgId, Guid? bookingId, string octorateThreadId, string guestName, DateTime? lastMessageAt = null) => new()
    {
        OrgId = orgId,
        BookingId = bookingId,
        OctorateThreadId = octorateThreadId,
        OctorateAccommodationId = "585492",
        ChannelSource = OtaChannel.Airbnb,
        Kind = bookingId is null ? OtaThreadKind.Request : OtaThreadKind.Reservation,
        GuestDisplayName = guestName,
        LastMessageAt = lastMessageAt ?? Now.AddHours(-3),
        LastMessageDirection = OtaMessageDirection.Inbound,
        CreatedAt = Now.AddDays(-30),
        UpdatedAt = Now.AddDays(-30),
    };

    private static OtaMessage NewMessage(
        OtaMessageThread thread,
        string octorateMessageId,
        OtaMessageDirection direction,
        string senderName,
        string text,
        string? attachmentUrl)
    {
        return new OtaMessage
        {
            OrgId = thread.OrgId,
            ThreadId = thread.Id,
            OctorateMessageId = octorateMessageId,
            Direction = direction,
            SenderName = senderName,
            Text = text,
            AttachmentUrl = attachmentUrl,
            AttachmentContentType = attachmentUrl is null ? null : "image/jpeg",
            SentAt = Now.AddHours(-3),
            DeliveryState = OtaMessageDeliveryState.Sent,
            Origin = direction == OtaMessageDirection.Inbound ? MessageOrigin.Guest : MessageOrigin.Person,
            CreatedAt = Now.AddHours(-3),
        };
    }
}
