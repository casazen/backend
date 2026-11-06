using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// A property of org A mirrored on Octorate (a connected account, a confirmed mapping with its room) and the push of its bookings
/// (E08/F01/US01/T03, T04), over an in-memory database, a strict client of Octorate the test sets up and a fixed clock.
/// </summary>
internal sealed class OctorateSyncHarness
{
    public const string Accommodation = "585492";
    public const string Room = "956528";
    public const string PmsRoom = "956527";

    public static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a71");
    public static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c72");

    /// <summary>2026-11-06 09:00 in Rome: the stays below are in December.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"octorate-sync-{Guid.NewGuid():N}";

    public OctorateSyncHarness()
    {
        using var seed = NewDb();
        var connection = new OctorateConnection { OrgId = OrgA, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        var property = NewProperty(OrgA, "Casa del Lago");
        seed.OctorateConnections.Add(connection);
        seed.Properties.Add(property);
        seed.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgA,
            ConnectionId = connection.Id,
            OctorateAccommodationId = Accommodation,
            OctorateProductId = Room,
            PropertyId = property.Id,
            Status = OctorateMappingStatus.Confirmed,
            SnapshotName = "Casa del Lago",
        });
        seed.SaveChanges();
        PropertyId = property.Id;

        Client
            .Setup(c => c.GetPmsRoomsAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new OctoratePmsRoom(PmsRoom, Room, "Sky 001 Real Room") });
    }

    public Guid PropertyId { get; }

    public Mock<IOctorateApiClient> Client { get; } = new(MockBehavior.Strict);

    public Mock<IInternalActivityService> Activities { get; } = new();

    public FixedTimeProvider Clock { get; } = new(Now);

    public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    public IConfiguration Configuration { get; } = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Octorate:DirectChannelId"] = "270",
            ["Octorate:PaymentModes:Stripe"] = "CREDITCARD",
            ["Octorate:PaymentModes:CashOnArrival"] = "CASH",
            ["Octorate:PaymentModes:BankTransfer"] = "BANKTRANSFER",
        })
        .Build();

    public AppDbContext NewDb(ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, tenant ?? NullTenantContext.Instance);

    /// <summary>The push in one scope of the event bus: the database, the org scope and the recorder of <paramref name="tenant"/>.</summary>
    public BookingOctorateSyncService Service(AppDbContext db, ITenantContext tenant) =>
        new(
            db,
            Client.Object,
            new OctorateOrgScope(tenant),
            Targets(db, tenant),
            Activities.Object,
            new DomainEventRecorder(db, tenant, Clock),
            Configuration,
            Clock,
            NullLogger<BookingOctorateSyncService>.Instance);

    public OctorateBookingTargets Targets(AppDbContext db, ITenantContext tenant)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var mapping = new ChannelMappingService(
            db,
            Client.Object,
            new OctorateOrgScope(tenant),
            new OctorateRateLimiter(Clock),
            Mock.Of<IHostScopeResolver>(),
            Mock.Of<IPropertyService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IInternalActivityService>(),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions()),
            host.Object,
            Clock,
            NullLogger<ChannelMappingService>.Instance);
        return new OctorateBookingTargets(
            db,
            Client.Object,
            new OctorateOrgScope(tenant),
            mapping,
            Cache,
            host.Object,
            Clock,
            NullLogger<OctorateBookingTargets>.Instance);
    }

    /// <summary>A confirmed booking of 10 to 13 December 2026, entered by the host, of 300 without the tax.</summary>
    public async Task<Guid> SeedBookingAsync(
        Guid? orgId = null,
        Guid? propertyId = null,
        BookingSource source = BookingSource.Manual,
        string bookingCode = "K7Q2M9X4TB")
    {
        await using var db = NewDb();
        var org = orgId ?? OrgA;
        var property = propertyId ?? PropertyId;
        if (org != OrgA && propertyId is null)
        {
            var other = NewProperty(org, "Casa di un'altra organizzazione");
            db.Properties.Add(other);
            property = other.Id;
        }

        var guest = new Guest { OrgId = org, FirstName = "Giulia", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = org,
            PropertyId = property,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 13, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = source,
            BasePrice = 300m,
            TouristTax = 6m,
            TotalPrice = 306m,
            BookingCode = bookingCode,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    public async Task<OctorateBookingSync?> ReadSyncAsync(Guid bookingId)
    {
        await using var db = NewDb();
        return await db.OctorateBookingSyncs.AsNoTracking().SingleOrDefaultAsync(s => s.BookingId == bookingId);
    }

    public async Task<Booking> ReadBookingAsync(Guid bookingId)
    {
        await using var db = NewDb();
        return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
    }

    private static Property NewProperty(Guid orgId, string name) => new()
    {
        OrgId = orgId,
        OwnerId = "auth0|owner",
        Name = name,
        Address = "Via del Lago 1",
        City = "Como",
        MaxGuests = 4,
        Currency = "EUR",
    };
}
