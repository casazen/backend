using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E05/F02/US01/T01: the conversations the showcase writes for the hub of messages, on a real PostgreSQL database, where the reset has to remove them.
/// A conversation of a channel points at its house and its stay with a foreign key that lets go (SET NULL), so left to the removal of the houses it
/// would stay behind as an unassigned conversation; the reset deletes the ones of the showcase by their markers first, with their messages (cascade),
/// and never touches a conversation a person made. The one with the guest of a direct stay goes with its booking.
/// </summary>
public class DemoMessageThreadsResetPostgresTests : IAsyncLifetime
{
    private static readonly DateTime At = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync("demomsgreset");

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task ResetOfTheConversations_DeletesTheShowcaseOnes_WithTheirMessagesAndMarkers_LeavesTheRealOneAlone_AndCanBeRepeated()
    {
        var world = await SeedAsync();

        // The reset of the conversations, twice: the second one finds nothing and changes nothing.
        await using (var db = NewDb())
        {
            var service = Service(db);
            await service.RemoveShowcaseMessageThreadsAsync(world.OrgId, CancellationToken.None);
            await service.RemoveShowcaseMessageThreadsAsync(world.OrgId, CancellationToken.None);
        }

        await using (var db = NewDb())
        {
            // Only the conversation a person made is left, with its message; the conversations of the showcase and their messages are gone.
            Assert.Equal(["REAL-1"], await db.OtaMessageThreads.IgnoreQueryFilters().Where(t => t.OrgId == world.OrgId).Select(t => t.OctorateThreadId).ToListAsync());
            Assert.Equal(["REAL-M1"], await db.OtaMessages.IgnoreQueryFilters().Where(m => m.OrgId == world.OrgId).Select(m => m.OctorateMessageId!).ToListAsync());

            // Their markers are gone, the others are not touched (the reset of the houses drops them).
            var markers = await db.TestDataMarkers.IgnoreQueryFilters().Where(m => m.OrgId == world.OrgId).Select(m => m.EntityType).ToListAsync();
            Assert.DoesNotContain(TestDataEntities.OtaMessageThread, markers);
            Assert.Equal(1, markers.Count(type => type == TestDataEntities.GuestThread));
            Assert.Equal(2, markers.Count(type => type == TestDataEntities.Property));
            Assert.Equal(2, markers.Count(type => type == TestDataEntities.Booking));

            // The conversation with the guest of the direct stay is still there: it goes with its booking.
            Assert.Equal(1, await db.GuestThreads.IgnoreQueryFilters().CountAsync(t => t.OrgId == world.OrgId));
        }

        // The houses go as the reset removes them: no showcase conversation is left behind as an unassigned one, and the direct one went with its stay.
        await using (var db = NewDb())
        {
            await db.Bookings.IgnoreQueryFilters().Where(b => b.OrgId == world.OrgId).ExecuteDeleteAsync();
            await db.Properties.IgnoreQueryFilters().Where(p => p.OrgId == world.OrgId).ExecuteDeleteAsync();
        }

        await using (var db = NewDb())
        {
            var left = await db.OtaMessageThreads.IgnoreQueryFilters().Where(t => t.OrgId == world.OrgId).ToListAsync();
            var real = Assert.Single(left);
            Assert.Equal("REAL-1", real.OctorateThreadId);
            Assert.Null(real.PropertyId);
            Assert.Equal(0, await db.GuestThreads.IgnoreQueryFilters().CountAsync(t => t.OrgId == world.OrgId));
        }
    }

    private AppDbContext NewDb() => new(_database!.CreateOptions(), tenantContext: null, new EphemeralDataProtectionProvider());

    private static DemoEnvironmentService Service(AppDbContext db) => new(
        db,
        Mock.Of<IFileStorage>(),
        Mock.Of<IPropertyComplianceStatusService>(),
        Options.Create(new ComplianceOptions()),
        Mock.Of<IDemoEnvironmentGate>(),
        NullLogger<DemoEnvironmentService>.Instance,
        new FixedTimeProvider(new DateTimeOffset(At)),
        Mock.Of<IServiceScopeFactory>(),
        Mock.Of<ICrmContactIndex>(),
        Mock.Of<IFeatureFlags>(),
        Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Development));

    /// <summary>
    /// An org with two showcase houses and a stay at each (the markers the reset leaves), the three conversations of the showcase written by the real seed
    /// code, and one conversation of a channel that a person's sync made, on the first house, with no marker.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var kit = new OrgInvitationTestKit(() => _database!.CreateContext());
        var (org, owner) = await kit.SeedOwnerOrgAsync();
        var orgId = org.Id;

        await using (var db = NewDb())
        {
            var lake = HostScopeScenario.NewProperty(orgId, owner.Id, "Villa Lago (demo)");
            var borgo = HostScopeScenario.NewProperty(orgId, owner.Id, "Casa Borgo di Prova (demo)");
            db.Properties.AddRange(lake, borgo);
            var guest = new Guest { OrgId = orgId, FirstName = "Laura", LastName = "Conti", Email = $"{Guid.NewGuid():N}@example.com" };
            db.Guests.Add(guest);
            var direct = Stay(orgId, lake.Id, guest, 8);
            var channel = Stay(orgId, borgo.Id, guest, 20);
            db.Bookings.AddRange(direct, channel);
            db.TestDataMarkers.AddRange(
                Marker(orgId, TestDataEntities.Property, lake.Id, "villa-lago"),
                Marker(orgId, TestDataEntities.Property, borgo.Id, "casa-borgo-prova"),
                Marker(orgId, TestDataEntities.Booking, direct.Id, "tassa-per-ospite"),
                Marker(orgId, TestDataEntities.Booking, channel.Id, "canale-riscuote"));

            var real = new OtaMessageThread
            {
                OrgId = orgId,
                PropertyId = lake.Id,
                OctorateThreadId = "REAL-1",
                OctorateAccommodationId = "ACC-REAL",
                ChannelSource = OtaChannel.Airbnb,
                Kind = OtaThreadKind.Request,
                GuestDisplayName = "Ospite Vero",
                LastMessageAt = At,
                LastMessageDirection = OtaMessageDirection.Inbound,
                UnreadCount = 1,
                CreatedAt = At,
                UpdatedAt = At,
            };
            db.OtaMessageThreads.Add(real);
            db.OtaMessages.Add(new OtaMessage
            {
                OrgId = orgId,
                ThreadId = real.Id,
                OctorateMessageId = "REAL-M1",
                Direction = OtaMessageDirection.Inbound,
                SenderName = "Ospite Vero",
                Text = "Una domanda vera",
                SentAt = At,
                DeliveryState = OtaMessageDeliveryState.Sent,
                Origin = MessageOrigin.Guest,
                CreatedAt = At,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var service = Service(db);
            foreach (var template in DemoShowcaseCatalog.MessageThreads)
                await service.CreateMessageThreadAsync(orgId, owner.Id, template, CancellationToken.None);
        }

        await using (var db = NewDb())
        {
            // The seed wrote what it should: the two conversations of channels of the showcase next to the real one, and the direct one with its marker.
            Assert.Equal(3, await db.OtaMessageThreads.IgnoreQueryFilters().CountAsync(t => t.OrgId == orgId));
            Assert.Equal(2, await db.TestDataMarkers.IgnoreQueryFilters().CountAsync(m => m.OrgId == orgId && m.EntityType == TestDataEntities.OtaMessageThread));
            Assert.Equal(1, await db.TestDataMarkers.IgnoreQueryFilters().CountAsync(m => m.OrgId == orgId && m.EntityType == TestDataEntities.GuestThread));
        }

        return new World(orgId);
    }

    private static Booking Stay(Guid orgId, Guid propertyId, Guest guest, int checkOutInDays) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        Guest = guest,
        Source = BookingSource.Direct,
        Status = BookingStatus.Confirmed,
        CheckInDate = At.Date.AddDays(checkOutInDays - 2),
        CheckOutDate = At.Date.AddDays(checkOutInDays),
    };

    private static TestDataMarker Marker(Guid orgId, string entityType, Guid entityId, string templateKey) => new()
    {
        OrgId = orgId,
        EntityType = entityType,
        EntityId = entityId,
        Origin = TestDataOrigins.Showcase,
        TemplateKey = templateKey,
    };

    private sealed record World(Guid OrgId);
}
