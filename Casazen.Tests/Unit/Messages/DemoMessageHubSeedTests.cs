using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US01/T01: the conversations the showcase writes for the hub of messages, written by the real seed code over the houses and stays of the
/// catalog (the markers the reset leaves) and read back by the hub itself: the counters of every thread agree with its messages, the rows are marked as
/// showcase data (the reset deletes them by their markers) and the hub lists all three as conversations to answer.
/// </summary>
public class DemoMessageHubSeedTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;
    private Guid _directBookingId;
    private Guid _channelBookingId;

    public async Task InitializeAsync()
    {
        _world = await HostScopeScenario.SeedAsync(_db);
        var orgId = _world.OrgId;

        // The two stays the conversations are about, as the reset creates them: a booking of the public checkout and one that came from Airbnb.
        var guest = new Guest { OrgId = orgId, FirstName = "Laura", LastName = "Conti", Email = "ospite.demo.tassa-ospiti@example.com" };
        _db.Guests.Add(guest);
        var direct = Stay(orgId, _world.Granted.Id, guest.Id, BookingSource.Direct);
        var channel = Stay(orgId, _world.Hidden.Id, guest.Id, BookingSource.Airbnb);
        _db.Bookings.AddRange(direct, channel);
        _directBookingId = direct.Id;
        _channelBookingId = channel.Id;

        _db.TestDataMarkers.AddRange(
            Marker(orgId, TestDataEntities.Property, _world.Granted.Id, "villa-lago"),
            Marker(orgId, TestDataEntities.Property, _world.Hidden.Id, "casa-borgo-prova"),
            Marker(orgId, TestDataEntities.Booking, direct.Id, "tassa-per-ospite"),
            Marker(orgId, TestDataEntities.Booking, channel.Id, "canale-riscuote"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static Booking Stay(Guid orgId, Guid propertyId, Guid guestId, BookingSource source) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        GuestId = guestId,
        CheckInDate = HostScopeScenario.Day(2026, 11, 8),
        CheckOutDate = HostScopeScenario.Day(2026, 11, 11),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
        Source = source,
        BasePrice = 300m,
        TotalPrice = 300m,
    };

    private static TestDataMarker Marker(Guid orgId, string entityType, Guid entityId, string templateKey) => new()
    {
        OrgId = orgId,
        EntityType = entityType,
        EntityId = entityId,
        Origin = TestDataOrigins.Showcase,
        TemplateKey = templateKey,
    };

    private DemoEnvironmentService Seeder() => new(
        _db,
        Mock.Of<IFileStorage>(),
        Mock.Of<IPropertyComplianceStatusService>(),
        Options.Create(new ComplianceOptions()),
        Mock.Of<IDemoEnvironmentGate>(),
        NullLogger<DemoEnvironmentService>.Instance,
        new FixedTimeProvider(MessageHubTestKit.Now),
        Mock.Of<IServiceScopeFactory>(),
        Mock.Of<ICrmContactIndex>(),
        Mock.Of<IFeatureFlags>());

    private async Task SeedAllAsync()
    {
        var seeder = Seeder();
        foreach (var template in DemoShowcaseCatalog.MessageThreads)
            await seeder.CreateMessageThreadAsync(_world.OrgId, "auth0|titolare", template, CancellationToken.None);
    }

    [Fact]
    public async Task TheShowcase_WritesTheThreeConversations_WithCountersThatAgreeWithTheirMessages_AndMarksThem()
    {
        await SeedAllAsync();

        // The direct conversation: one message of the guest, unread, so it waits for an answer.
        var direct = await _db.GuestThreads.AsNoTracking().SingleAsync(t => t.BookingId == _directBookingId);
        Assert.Equal(_world.Granted.Id, direct.PropertyId);
        Assert.NotNull(direct.LastGuestMessageAt);
        Assert.Null(direct.LastHostMessageAt);
        Assert.Null(direct.HostReadAt);
        Assert.Equal(direct.LastGuestMessageAt, direct.LastMessageAt);
        var directMessage = await _db.GuestMessages.AsNoTracking().SingleAsync(m => m.ThreadId == direct.Id);
        Assert.Equal(GuestMessageSender.Guest, directMessage.Sender);
        Assert.Contains("TEST", directMessage.Text, StringComparison.Ordinal);

        // The conversation of Airbnb about the stay: a read question, an answer, a new question; one unread, the guest wrote last.
        var reservation = await _db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.BookingId == _channelBookingId);
        Assert.Equal(_world.Hidden.Id, reservation.PropertyId);
        Assert.Equal(OtaChannel.Airbnb, reservation.ChannelSource);
        Assert.Equal(OtaThreadKind.Reservation, reservation.Kind);
        Assert.Equal(OtaMessageDirection.Inbound, reservation.LastMessageDirection);
        Assert.Equal(1, reservation.UnreadCount);
        Assert.Equal("Jonas B. (TEST)", reservation.GuestDisplayName);
        var reservationMessages = await _db.OtaMessages.AsNoTracking().Where(m => m.ThreadId == reservation.Id).OrderBy(m => m.SentAt).ToListAsync();
        Assert.Equal(
            [OtaMessageDirection.Inbound, OtaMessageDirection.Outbound, OtaMessageDirection.Inbound],
            reservationMessages.Select(m => m.Direction));
        Assert.Equal(reservation.LastMessageAt, reservationMessages[^1].SentAt);
        Assert.Equal(1, reservationMessages.Count(m => m.Direction == OtaMessageDirection.Inbound && m.ReadAt is null));
        Assert.All(reservationMessages, m => Assert.Equal(OtaMessageDeliveryState.Sent, m.DeliveryState));
        Assert.Equal(reservationMessages.Select(m => m.OctorateMessageId).Distinct().Count(), reservationMessages.Count);

        // The request of Booking.com: no booking, the guest wrote and nobody answered.
        var request = await _db.OtaMessageThreads.AsNoTracking().SingleAsync(t => t.BookingId == null);
        Assert.Equal(_world.Granted.Id, request.PropertyId);
        Assert.Equal(OtaChannel.Booking, request.ChannelSource);
        Assert.Equal(OtaThreadKind.Request, request.Kind);
        Assert.Equal(1, request.UnreadCount);
        Assert.Equal(OtaMessageDirection.Inbound, request.LastMessageDirection);

        // Everything invented says TEST, and the threads of the channel manager have ids that cannot be a real one.
        Assert.All(await _db.OtaMessages.AsNoTracking().ToListAsync(), m =>
        {
            Assert.Contains("TEST", m.Text, StringComparison.Ordinal);
            Assert.StartsWith("TEST-", m.OctorateMessageId, StringComparison.Ordinal);
        });
        Assert.All(new[] { reservation, request }, t => Assert.StartsWith("TEST-", t.OctorateThreadId, StringComparison.Ordinal));

        // The reset finds them by their markers: the two threads of the channels and the direct one.
        var markers = await _db.TestDataMarkers.AsNoTracking().Where(m => m.Origin == TestDataOrigins.Showcase).ToListAsync();
        Assert.Equal(
            [reservation.Id, request.Id],
            markers.Where(m => m.EntityType == TestDataEntities.OtaMessageThread).Select(m => m.EntityId).Order());
        Assert.Equal([direct.Id], markers.Where(m => m.EntityType == TestDataEntities.GuestThread).Select(m => m.EntityId));
    }

    [Fact]
    public async Task TheHub_ListsTheThreeConversationsOfTheShowcase_AllToAnswer_AndTheDetailsOpen()
    {
        await SeedAllAsync();
        var hub = MessageHubTestKit.Service(_db);

        var page = await hub.ListAsync(_world.OrgWide, new MessageHubFilter(NeedsReply: true), 1, 20);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(
            [MessageThreadSource.Direct, MessageThreadSource.Ota, MessageThreadSource.Ota],
            page.Items.Select(i => i.Source).Order());
        Assert.Contains(page.Items, i => i.Kind == MessageHubThreadKind.Request && i.BookingId is null && !i.Unassigned);
        Assert.Contains(page.Items, i => i.Channel == MessageHubChannel.Airbnb && i.BookingId == _channelBookingId);
        Assert.Contains(page.Items, i => i.Channel == MessageHubChannel.Direct && i.BookingId == _directBookingId && i.UnreadCount == 1);
        Assert.All(page.Items, i => Assert.Contains("TEST", i.Preview, StringComparison.Ordinal));

        foreach (var item in page.Items.Where(i => i.Source == MessageThreadSource.Ota))
        {
            var thread = await hub.GetThreadAsync(_world.OrgWide, item.Key, includeAmounts: true);
            Assert.NotEmpty(thread.Messages);
        }
    }

    [Fact]
    public async Task ATemplateThatNamesAStayThatWasNotCreated_IsSkipped_WritingNothing()
    {
        var template = DemoShowcaseCatalog.MessageThreads.First(t => t.Kind == DemoMessageThreadKind.OtaReservation) with { BookingKey = "no-such-stay" };

        await Seeder().CreateMessageThreadAsync(_world.OrgId, "auth0|titolare", template, CancellationToken.None);

        Assert.Empty(await _db.OtaMessageThreads.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.OtaMessages.AsNoTracking().ToListAsync());
    }
}
