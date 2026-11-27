using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace Casazen.Tests.Unit.Messages;

/// <summary>An <see cref="IEmailQueue"/> that keeps what it was given, as the Hangfire queue would receive it.</summary>
internal sealed class RecordingEmailQueue : IEmailQueue
{
    public List<(string? To, EmailContent Content, string Template)> Sent { get; } = [];

    /// <summary>What <see cref="Enqueue"/> answers: false is a queue that could not take the email.</summary>
    public bool Accepts { get; set; } = true;

    public bool Enqueue(string? to, EmailContent content, string template)
    {
        Sent.Add((to, content, template));
        return Accepts;
    }
}

/// <summary>
/// The service of the reply (E05/F02/US02/T01) over the data of <see cref="MessageHubTestKit"/>, with every neighbour a test can steer or watch: the Octorate
/// client, the direct service, the mapping, the targets, the queue of the e-mails, the clock, the log. The activity log and the cache are the real ones.
/// </summary>
internal sealed class MessageReplyRig
{
    public MessageReplyRig(AppDbContext db, TimeProvider? clock = null)
    {
        Db = db;
        Flags = new TestFeatureFlags(FeatureFlags.OctorateConnector);
        Service = new MessageReplyService(
            db,
            Direct.Object,
            Mapping.Object,
            Client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            Targets.Object,
            Flags,
            Emails,
            new ActivityLog(db),
            Cache,
            Log,
            clock ?? new FixedTimeProvider(MessageHubTestKit.Now));
    }

    public AppDbContext Db { get; }

    public Mock<IOctorateApiClient> Client { get; } = new();

    public Mock<IGuestMessageService> Direct { get; } = new();

    public Mock<IChannelMappingService> Mapping { get; } = new();

    public Mock<IOctorateBookingTargets> Targets { get; } = new();

    public RecordingEmailQueue Emails { get; } = new();

    public CapturingLogger<MessageReplyService> Log { get; } = new();

    public TestFeatureFlags Flags { get; }

    public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

    public MessageReplyService Service { get; }

    /// <summary>The channel manager accepts every message and says it is sent, under the id <paramref name="messageId"/>.</summary>
    public void ChannelManagerAccepts(string messageId = "90001", OtaMessageDeliveryState state = OtaMessageDeliveryState.Sent) =>
        Client
            .Setup(c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChatSendResult(messageId, state));

    public void NothingWasSent() =>
        Client.Verify(
            c => c.SendChatMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
}

/// <summary>Bookings of the kinds the reply cares about, added to the world of <see cref="MessageHubTestKit"/>.</summary>
internal static class MessageReplyScenario
{
    /// <summary>A guest of the org with an address and a number the tests look for in every answer, log and row.</summary>
    public const string GuestEmail = "mei.tanaka@guest-mail.example";

    public const string GuestPhone = "+81 90 1234 5678";

    public static async Task<Guest> AddGuestAsync(AppDbContext db, Guid orgId, string first, string last, string country = "IT")
    {
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = first,
            LastName = last,
            Email = $"{Guid.NewGuid():N}@example.com",
            Country = country,
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return guest;
    }

    public static async Task<Booking> AddBookingAsync(
        AppDbContext db,
        MessageHubWorld world,
        Guid propertyId,
        Guest guest,
        BookingSource source,
        string? octorateReservationId,
        DateTime? checkOut = null,
        BookingStatus status = BookingStatus.Confirmed,
        DateTime? checkInAt = null)
    {
        var checkIn = checkInAt ?? HostScopeScenario.Day(2026, 10, 20);
        var booking = new Booking
        {
            OrgId = world.Scope.OrgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut ?? checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = source,
            OctorateReservationId = octorateReservationId,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return booking;
    }
}
