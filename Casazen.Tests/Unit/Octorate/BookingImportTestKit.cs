using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>The values the tests of the bookings of the channel manager share (E02/F02/US01).</summary>
internal static class BookingImportTestKit
{
    public const string Accommodation = "585492";
    public const string Room = "956528";

    public static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a11");
    public static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c22");

    /// <summary>2026-11-06 09:00 in Rome: the stays below start on 20 November.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    public static readonly DateTime FirstVersion = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime SecondVersion = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A confirmed reservation of Airbnb on the room of the property, 20 to 22 November 2026, with a commission of 45 and 100 paid.</summary>
    public static OctorateReservation Res(
        string id,
        OctorateBookingStatus status = OctorateBookingStatus.Confirmed,
        string? product = Room) => new()
        {
            Id = id,
            Status = status,
            Product = product,
            ChannelId = "274",
            ChannelName = "Airbnb",
            CheckIn = "2026-11-19T23:00:00Z",
            CheckOut = "2026-11-22T10:00:00Z",
            UpdatedAt = FirstVersion,
            FirstName = "Giulia",
            LastName = "Rossi",
            TotalGuests = 2,
            RoomGross = 300m,
            TotalGross = 330m,
            TouristTax = 6m,
            TotalPaid = 100m,
            PriceBreakdown = [new OctoratePriceComponent("COMMISSION", 45m)],
        };
}

/// <summary>The tenant of an authenticated request of one org.</summary>
internal sealed class RequestTenant(Guid orgId) : ITenantContext
{
    public Guid? OrgId { get; } = orgId;

    public bool FilterEnabled => true;
}

/// <summary>A property of org A with a listing mapped to it, an Octorate client that answers what the test serves, and a clock.</summary>
internal sealed class BookingImportHarness
{
    private readonly string _database = $"octorate-import-{Guid.NewGuid():N}";

    public BookingImportHarness(
        string? room = BookingImportTestKit.Room,
        OctorateMappingStatus status = OctorateMappingStatus.Confirmed,
        string timezone = "Europe/Rome",
        string currency = "EUR")
    {
        using var seed = NewDb();
        var connection = new OctorateConnection
        {
            OrgId = BookingImportTestKit.OrgA,
            Environment = OctorateEnvironment.Test,
            Status = OctorateConnectionStatus.Connected,
        };
        var property = new Property
        {
            OrgId = BookingImportTestKit.OrgA,
            OwnerId = "auth0|owner-a",
            Name = "Casa del Lago",
            Address = "Via del Lago 1",
            City = "Como",
            MaxGuests = 4,
            Timezone = timezone,
            Currency = currency,
        };
        seed.OctorateConnections.Add(connection);
        seed.Properties.Add(property);
        var mapping = new OctorateListingMapping
        {
            OrgId = BookingImportTestKit.OrgA,
            ConnectionId = connection.Id,
            OctorateAccommodationId = BookingImportTestKit.Accommodation,
            OctorateProductId = room,
            PropertyId = property.Id,
            Status = status,
            SnapshotName = "Casa del Lago",
        };
        seed.OctorateListingMappings.Add(mapping);
        seed.SaveChanges();
        PropertyId = property.Id;
        MappingId = mapping.Id;
        ConnectionId = connection.Id;
    }

    public Guid PropertyId { get; }

    public Guid MappingId { get; }

    public Guid ConnectionId { get; }

    public FakeTimeProvider Clock { get; } = new(BookingImportTestKit.Now);

    public Mock<IOctorateApiClient> Client { get; } = new();

    public AppDbContext NewDb(ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, tenant ?? NullTenantContext.Instance);

    public OctorateOrgScope Scope(ITenantContext? tenant = null) => new(tenant ?? NullTenantContext.Instance);

    public BookingImportService Service(AppDbContext db, ITenantContext? tenant = null) =>
        new(
            db,
            Client.Object,
            Scope(tenant),
            new OctorateRateLimiter(Clock),
            new BookingStateMapper(),
            new DomainEventRecorder(db, tenant ?? NullTenantContext.Instance, Clock),
            Clock,
            NullLogger<BookingImportService>.Instance);

    /// <summary>The list answers these reservations on its first page (and an empty page after it); the single read answers each one.</summary>
    public void Serve(params OctorateReservation[] reservations)
    {
        Client.Reset();
        ServeMore(BookingImportTestKit.Accommodation, reservations);
    }

    /// <summary>Like <see cref="Serve"/> for another accommodation, without forgetting what was served before.</summary>
    public void ServeMore(string accommodation, params OctorateReservation[] reservations)
    {
        Client
            .Setup(c => c.GetReservationsAsync(accommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, OctorateReservationQuery query, CancellationToken _) =>
                query.Page == 0 ? OctorateReservationPage.Of(reservations) : OctorateReservationPage.Empty);
        foreach (var reservation in reservations)
        {
            var served = reservation;
            Client
                .Setup(c => c.GetReservationAsync(accommodation, served.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(served);
        }
    }

    /// <summary>Another property of org A, with a confirmed listing of its own (one room), on the connection of the harness.</summary>
    public async Task<Guid> AddListingAsync(string accommodation, string room)
    {
        await using var db = NewDb();
        var property = new Property
        {
            OrgId = BookingImportTestKit.OrgA,
            OwnerId = "auth0|owner-a",
            Name = "Casa " + accommodation,
            Address = "Via Prova 1",
            City = "Como",
            MaxGuests = 4,
        };
        db.Properties.Add(property);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = BookingImportTestKit.OrgA,
            ConnectionId = ConnectionId,
            OctorateAccommodationId = accommodation,
            OctorateProductId = room,
            PropertyId = property.Id,
            Status = OctorateMappingStatus.Confirmed,
            SnapshotName = property.Name,
        });
        await db.SaveChangesAsync();
        return property.Id;
    }

    public async Task<Guid> SeedBookingAsync(
        string? octorateId,
        Guid? orgId = null,
        Guid? propertyId = null,
        BookingSource source = BookingSource.Airbnb,
        BookingStatus status = BookingStatus.Confirmed,
        decimal total = 100m,
        DateTime? updatedAt = null,
        DateTime? octorateUpdatedAt = null)
    {
        await using var db = NewDb();
        var org = orgId ?? BookingImportTestKit.OrgA;
        var guest = new Guest { OrgId = org, FirstName = "Seed", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = org,
            PropertyId = propertyId ?? PropertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = source,
            OctorateReservationId = octorateId,
            OctorateUpdatedAt = octorateUpdatedAt,
            BasePrice = total,
            TotalPrice = total,
            UpdatedAt = updatedAt ?? BookingImportTestKit.FirstVersion,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    public async Task<Guid> SeedGuestAsync(string firstName, string lastName, string email)
    {
        await using var db = NewDb();
        var guest = new Guest
        {
            OrgId = BookingImportTestKit.OrgA,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            CreatedAt = BookingImportTestKit.FirstVersion.AddDays(-30),
        };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return guest.Id;
    }

    public async Task<Booking> ReadBookingAsync(string octorateId)
    {
        await using var db = NewDb();
        return await db.Bookings.AsNoTracking().SingleAsync(b => b.OctorateReservationId == octorateId && b.OrgId == BookingImportTestKit.OrgA);
    }

    public async Task<DateTime?> ReadCheckpointAsync()
    {
        await using var db = NewDb();
        return (await db.OctorateListingMappings.AsNoTracking().SingleAsync()).BookingsImportedThrough;
    }

    public async Task<List<OutboxEvent>> ReadEventsAsync()
    {
        await using var db = NewDb();
        return await db.OutboxEvents.AsNoTracking().ToListAsync();
    }
}
