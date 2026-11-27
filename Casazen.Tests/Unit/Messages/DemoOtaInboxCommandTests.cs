using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

public class DemoOtaInboxCommandTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(BookingSource.Airbnb, OtaChannel.Airbnb)]
    [InlineData(BookingSource.BookingCom, OtaChannel.Booking)]
    [InlineData(BookingSource.Expedia, OtaChannel.Expedia)]
    [InlineData(BookingSource.Vrbo, OtaChannel.Vrbo)]
    public void Channel_MapsTheNamedChannelRatherThanCastingItsDifferentNumericValue(BookingSource source, OtaChannel expected) =>
        Assert.Equal(expected, DemoOtaInboxCommand.Channel(source));

    [Fact]
    public async Task Import_RejectsUnmarkedRealBookingsWithoutWritingInboxRows()
    {
        await using var db = NewDb();
        var (booking, payload) = await SeedAsync(db, marked: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoOtaInboxCommand.ImportAsync(db, payload, Now));
        Assert.Empty(await db.OtaMessageThreads.ToListAsync());
        Assert.Empty(await db.OtaMessages.ToListAsync());
    }

    [Fact]
    public async Task Import_LinksAnOtaThreadAndPreservesReadMarksWhenTheSnapshotIsReplayed()
    {
        await using var db = NewDb();
        var (booking, payload) = await SeedAsync(db, marked: true);
        Assert.Equal((1, 3), await DemoOtaInboxCommand.ImportAsync(db, payload, Now));
        var thread = await db.OtaMessageThreads.SingleAsync();
        Assert.Equal(booking.Id, thread.BookingId);
        Assert.Equal(booking.PropertyId, thread.PropertyId);
        Assert.Equal(OtaChannel.Expedia, thread.ChannelSource);
        Assert.Equal(OtaMessageDirection.Inbound, thread.LastMessageDirection);
        Assert.Equal(1, thread.UnreadCount);
        var messages = await db.OtaMessages.OrderBy(m => m.SentAt).ToListAsync();
        Assert.Equal(MessageOrigin.Assistant, messages[1].Origin);
        Assert.Equal("check_in_time", messages[1].RuleCode);
        Assert.Equal(OtaMessageDeliveryState.Sent, messages[1].DeliveryState);
        Assert.NotNull(messages[0].ReadAt);
        Assert.Null(messages[2].ReadAt);
        thread.UnreadCount = 0;
        messages[2].ReadAt = Now;
        await db.SaveChangesAsync();
        Assert.Equal((0, 0), await DemoOtaInboxCommand.ImportAsync(db, payload, Now));
        Assert.Equal(3, await db.OtaMessages.CountAsync());
        Assert.Equal(0, (await db.OtaMessageThreads.SingleAsync()).UnreadCount);
        Assert.Equal(Now, (await db.OtaMessages.SingleAsync(m => m.Id == messages[2].Id)).ReadAt);
        Assert.Equal(4, await db.TestDataMarkers.CountAsync(m => m.Origin == DemoOtaInboxCommand.MarkerOrigin));
    }

    [Fact]
    public async Task Import_RejectsCrossTenantBookingAndFutureDatedMessages()
    {
        await using var db = NewDb();
        var (_, payload) = await SeedAsync(db, marked: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoOtaInboxCommand.ImportAsync(db, payload with { OrgId = Guid.NewGuid() }, Now));
        var conversation = payload.Conversations[0];
        conversation.Messages[^1] = conversation.Messages[^1] with { SentAt = Now.AddMinutes(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoOtaInboxCommand.ImportAsync(db, payload, Now));
        Assert.Empty(await db.OtaMessageThreads.ToListAsync());
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Booking Booking, DemoOtaInboxCommand.Payload Payload)> SeedAsync(AppDbContext db, bool marked)
    {
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Guest TEST", LastName = "Demo" };
        var booking = new Booking { OrgId = orgId, PropertyId = Guid.NewGuid(), GuestId = guest.Id, Source = BookingSource.Expedia };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        if (marked)
            db.TestDataMarkers.Add(new TestDataMarker { OrgId = orgId, EntityId = booking.Id, EntityType = "Booking", Origin = "unit-test" });
        await db.SaveChangesAsync();
        var conversation = new DemoOtaInboxCommand.Conversation(booking.Id, "990000000001", "990000000002", "990000000003",
        [
            new("990000000004", "A che ora si entra?", Now.AddHours(-3), false),
            new("990000000005", "Check-in dalle 15:00.", Now.AddHours(-2), true, "check_in_time"),
            new("990000000006", "Possiamo lasciare i bagagli prima?", Now.AddHours(-1), false),
        ]);
        return (booking, new DemoOtaInboxCommand.Payload(orgId, [conversation]));
    }
}
