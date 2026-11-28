using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The copy of the OTA conversations of org A (E05/F01/US01): a connected account, listings with the mapping the test sets, a strict client of
/// Octorate the test sets up, a fixed clock and a log sink, over an in-memory database. The service is built as the container builds it, one
/// per delivery, on its own context of the same database.
/// </summary>
internal sealed class MessageSyncHarness
{
    public const string Accommodation = "585492";

    public static readonly Guid OrgA = Guid.Parse("5b0f3a2e-7c41-4d68-9e15-0a2b3c4d5e81");
    public static readonly Guid OrgB = Guid.Parse("c3d2e1f0-9a8b-4c7d-8e6f-5a4b3c2d1e82");

    public static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"message-sync-{Guid.NewGuid():N}";
    private readonly List<ChannelMappingRow> _rows = [];

    public MessageSyncHarness()
    {
        Limiter = new OctorateRateLimiter(Clock);
        using var seed = NewDb();
        var connection = new OctorateConnection { OrgId = OrgA, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        seed.OctorateConnections.Add(connection);
        seed.SaveChanges();
        ConnectionId = connection.Id;

        Mappings
            .Setup(m => m.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid org, CancellationToken _) => (IReadOnlyList<ChannelMappingRow>)(org == OrgA ? _rows.ToList() : []));
        Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        PropertyId = AddListing(Accommodation, OctorateMappingStatus.Confirmed)!.Value;
    }

    public Guid ConnectionId { get; }

    public Guid PropertyId { get; }

    public Mock<IOctorateApiClient> Client { get; } = new(MockBehavior.Strict);

    public Mock<IChannelMappingService> Mappings { get; } = new();

    public Mock<IFeatureFlags> Flags { get; } = new();

    public FixedTimeProvider Clock { get; } = new(Now);

    public CapturingLogger<MessageSyncService> Log { get; } = new();

    public OctorateRateLimiter Limiter { get; }

    public AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

    public MessageSyncService Service(AppDbContext db, MessageSyncPacing? pacing = null) =>
        new(
            db,
            Client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            Mappings.Object,
            new DomainEventRecorder(db, NullTenantContext.Instance, Clock),
            Flags.Object,
            Limiter,
            pacing ?? new MessageSyncPacing(),
            Clock,
            Log);

    public Guid? AddListing(string accommodation, OctorateMappingStatus status)
    {
        using var db = NewDb();
        Guid? propertyId = null;
        if (status == OctorateMappingStatus.Confirmed)
        {
            var property = new Property
            {
                OrgId = OrgA,
                OwnerId = "auth0|owner",
                Name = $"Casa {accommodation}",
                Address = "Via del Lago 1",
                City = "Como",
                MaxGuests = 4,
                Currency = "EUR",
            };
            db.Properties.Add(property);
            propertyId = property.Id;
        }

        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgA,
            ConnectionId = ConnectionId,
            OctorateAccommodationId = accommodation,
            PropertyId = propertyId,
            Status = status,
        });
        db.SaveChanges();

        _rows.Add(Row(accommodation, status, propertyId));
        return propertyId;
    }

    public void Confirm(string accommodation, Guid propertyId)
    {
        _rows.RemoveAll(r => r.OctorateAccommodationId == accommodation);
        _rows.Add(Row(accommodation, OctorateMappingStatus.Confirmed, propertyId));
    }

    public static ChannelMappingRow Row(string accommodation, OctorateMappingStatus status, Guid? propertyId) => new(
        Guid.NewGuid(),
        accommodation,
        $"Casa {accommodation}",
        "Via del Lago 1",
        "Como",
        4,
        [],
        null,
        status,
        100,
        propertyId is { } id ? new ChannelMappingProperty(id, $"Casa {accommodation}", "Via del Lago 1", "Como") : null,
        [],
        [],
        null);

    public static OctorateChatThread Thread(string id, string accommodation = Accommodation, string? reservationId = "R-4711", string channelId = "274") => new()
    {
        Id = id,
        AccommodationId = accommodation,
        DefaultProcessor = "CHANNEL",
        AiDriven = false,
        ReservationId = reservationId,
        ReservationChannelId = reservationId is null ? null : channelId,
        GuestName = "Giulietta Verdi",
        LastActivityAt = Now.UtcDateTime.AddHours(-1),
    };

    public static OctorateChatMessage Inbound(string id, string threadId, string text = "Buongiorno, a che ora e il check-in?") => new()
    {
        Id = id,
        ThreadId = threadId,
        Processor = "CHANNEL",
        Direction = OtaMessageDirection.Inbound,
        SenderName = "Giulietta Verdi",
        Text = text,
        SentAt = Now.UtcDateTime.AddHours(-1),
        DeliveryState = OtaMessageDeliveryState.Sent,
    };

    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        await using var db = NewDb();
        return await read(db);
    }
}
