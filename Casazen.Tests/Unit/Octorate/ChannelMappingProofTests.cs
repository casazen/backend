using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the mapping of the listings (E02/F01/US02/T03): nothing is written to a property while its mapping is proposed or
/// ambiguous (data rule, OCT-04); a person confirms only to a property of the org within its reach (permission rule, ADR-023-R4); the
/// import never creates anything on Octorate (data rule, ADR-027-R8).
/// </summary>
public class ChannelMappingProofTests
{
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a01");
    private static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c02");
    private const string Admin = "auth0|admin-a";
    private const string Collaborator = "auth0|collaborator-a";

    [Fact]
    public async Task ChannelMapping_UnconfirmedBlocksWrites_Test()
    {
        await using var db = NewDb();
        var connection = await SeedConnectionAsync(db, OrgA);
        var proposed = await SeedPropertyAsync(db, OrgA, "Casa Proposta");
        var candidate = await SeedPropertyAsync(db, OrgA, "Casa Candidata");
        var confirmed = await SeedPropertyAsync(db, OrgA, "Casa Confermata");
        var free = await SeedPropertyAsync(db, OrgA, "Casa Senza Annuncio");
        db.OctorateListingMappings.AddRange(
            Mapping(OrgA, connection, "1001", OctorateMappingStatus.Proposed, proposed),
            Mapping(OrgA, connection, "1002", OctorateMappingStatus.Ambiguous, null, $"[\"{candidate}\",\"{Guid.NewGuid()}\"]"),
            Mapping(OrgA, connection, "1003", OctorateMappingStatus.Confirmed, confirmed));
        await db.SaveChangesAsync();
        var service = Service(db, new Mock<IOctorateApiClient>(MockBehavior.Strict).Object);

        // Proposed or among the candidates of an ambiguous row: blocked until a person confirms.
        Assert.True(await service.IsWriteBlockedAsync(proposed, CancellationToken.None));
        Assert.True(await service.IsWriteBlockedAsync(candidate, CancellationToken.None));
        // Confirmed: writes allowed. A property in no row is not blocked by the mapping (it has no listing to write to).
        Assert.False(await service.IsWriteBlockedAsync(confirmed, CancellationToken.None));
        Assert.False(await service.IsWriteBlockedAsync(free, CancellationToken.None));
        // A property CasaZen does not know: nothing is written for it.
        Assert.True(await service.IsWriteBlockedAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ChannelMapping_ConfirmChecksOrgAndReach_Test()
    {
        await using var db = NewDb();
        var connection = await SeedConnectionAsync(db, OrgA);
        var mine = await SeedPropertyAsync(db, OrgA, "Casa del Lago");
        var granted = await SeedPropertyAsync(db, OrgA, "Casa del Bosco");
        var otherOrg = await SeedPropertyAsync(db, OrgB, "Casa di un'altra organizzazione");
        db.OctorateListingMappings.Add(Mapping(OrgA, connection, "1001", OctorateMappingStatus.Proposed, mine));
        await db.SaveChangesAsync();

        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        client.Setup(c => c.GetRoomsAsync("1001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateRoom { Id = "956528", Name = "Double Room", Adults = 2 }]);
        client.Setup(c => c.GetConnectionsAsync("1001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateChannelConnection("77", "142", "Booking.com", "BK-123456", "ACTIVE")]);
        var service = Service(db, client.Object, Resolver(granted));

        // A collaborator «Solo alcuni» who was given another property cannot confirm the listing to this one.
        var unreachable = await Assert.ThrowsAsync<NotFoundException>(
            () => service.ConfirmAsync(OrgA, "1001", mine, null, Collaborator, CancellationToken.None));
        Assert.Equal("octorate_property_not_found", unreachable.Code);

        // Nobody confirms a listing to a property of another organization, an administrator neither.
        var foreign = await Assert.ThrowsAsync<NotFoundException>(
            () => service.ConfirmAsync(OrgA, "1001", otherOrg, null, Admin, CancellationToken.None));
        Assert.Equal("octorate_property_not_found", foreign.Code);

        // Refused before anything was read or written.
        client.VerifyNoOtherCalls();
        var untouched = await db.OctorateListingMappings.AsNoTracking().SingleAsync();
        Assert.Equal(OctorateMappingStatus.Proposed, untouched.Status);
        Assert.Null(untouched.ConfirmedByUserId);

        // An administrator of the org confirms it: user, time, the only room and the channel identifiers as Octorate gives them.
        await service.ConfirmAsync(OrgA, "1001", mine, null, Admin, CancellationToken.None);
        var confirmed = await db.OctorateListingMappings.AsNoTracking().SingleAsync();
        Assert.Equal(OctorateMappingStatus.Confirmed, confirmed.Status);
        Assert.Equal(mine, confirmed.PropertyId);
        Assert.Equal(Admin, confirmed.ConfirmedByUserId);
        Assert.NotNull(confirmed.ConfirmedAt);
        Assert.Equal("956528", confirmed.OctorateProductId);
        Assert.Equal("""{"142":"BK-123456"}""", confirmed.ChannelIdsJson);

        // The property is now confirmed to this listing: another listing cannot take it.
        db.OctorateListingMappings.Add(Mapping(OrgA, connection, "1002", OctorateMappingStatus.Proposed, mine));
        await db.SaveChangesAsync();
        var taken = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.ConfirmAsync(OrgA, "1002", mine, null, Admin, CancellationToken.None));
        Assert.Equal("octorate_mapping_property_taken", taken.Code);
    }

    [Fact]
    public async Task ChannelMapping_ImportNeverCreatesOctorateProperty_Test()
    {
        await using var db = NewDb();
        var connection = await SeedConnectionAsync(db, OrgA);
        db.OctorateListingMappings.Add(Mapping(OrgA, connection, "1001", OctorateMappingStatus.Proposed, null));
        await db.SaveChangesAsync();

        // Strict: any call the setups below do not name (a create, an update, a write of any kind) throws.
        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        client.Setup(c => c.GetPropertyAsync("1001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateProperty { Id = "1001", Name = "Casa del Lago", Address = "Via del Lago 1", City = "Como", PostalCode = "22100", Country = "IT" });
        client.Setup(c => c.GetRoomsAsync("1001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateRoom { Id = "956528", Name = "Double Room", Adults = 2, Children = 1, Bedrooms = 1, Bathrooms = 1, Beds = 2 }]);
        client.Setup(c => c.GetConnectionsAsync("1001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateChannelConnection("78", "274", "Airbnb", "AB-987654", "ACTIVE")]);

        var properties = new Mock<IPropertyService>();
        properties.Setup(p => p.CreatePropertyAsync(It.IsAny<Property>()))
            .Returns(async (Property property) =>
            {
                db.Properties.Add(property);
                await db.SaveChangesAsync();
                return property;
            });
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.CanAddPropertyAsync(OrgA, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        entitlements.Setup(e => e.CreatePropertyWithinLimitAsync(OrgA, It.IsAny<Func<Task<Property>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Func<Task<Property>> create, CancellationToken _) => (Property?)await create());
        var activities = new Mock<IInternalActivityService>();
        activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());

        var service = Service(db, client.Object, propertyService: properties.Object, entitlements: entitlements.Object, activities: activities.Object);

        var propertyId = await service.ImportAsPropertyAsync(OrgA, "1001", Admin, CancellationToken.None);

        // Only reads reached Octorate: the accommodation, its rooms and its channel connections.
        client.Verify(c => c.GetPropertyAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetRoomsAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetConnectionsAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();

        // The CasaZen property: created through the property create path, in the org, by the person, inactive.
        properties.Verify(p => p.CreatePropertyAsync(It.IsAny<Property>()), Times.Once);
        var created = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
        Assert.Equal(OrgA, created.OrgId);
        Assert.Equal(Admin, created.OwnerId);
        Assert.False(created.IsActive);
        Assert.Equal("Casa del Lago", created.Name);
        Assert.Equal(3, created.MaxGuests);

        // The mapping is confirmed at once, with the room and the identifier the listing already has on the channel.
        var mapping = await db.OctorateListingMappings.AsNoTracking().SingleAsync();
        Assert.Equal(OctorateMappingStatus.Confirmed, mapping.Status);
        Assert.Equal(propertyId, mapping.PropertyId);
        Assert.Equal("956528", mapping.OctorateProductId);
        Assert.Equal("""{"274":"AB-987654"}""", mapping.ChannelIdsJson);

        // Flagged to check: a To do on the new property.
        activities.Verify(a => a.CreateAsync(
            It.Is<CreateTodoRequest>(r => r.Type == ActivityType.CheckImportedListing && r.PropertyId == propertyId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ChannelMappingService Service(
        AppDbContext db,
        IOctorateApiClient client,
        IHostScopeResolver? resolver = null,
        IPropertyService? propertyService = null,
        IEntitlementService? entitlements = null,
        IInternalActivityService? activities = null)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        return new ChannelMappingService(
            db,
            client,
            new OctorateOrgScope(NullTenantContext.Instance),
            new OctorateRateLimiter(TimeProvider.System),
            resolver ?? Resolver(),
            propertyService ?? Mock.Of<IPropertyService>(),
            entitlements ?? Mock.Of<IEntitlementService>(),
            activities ?? Mock.Of<IInternalActivityService>(),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions()),
            host.Object,
            TimeProvider.System,
            NullLogger<ChannelMappingService>.Instance);
    }

    /// <summary>
    /// The real scope resolver over the org team in the database: <see cref="Admin"/> is an administrator of org A (every property),
    /// <see cref="Collaborator"/> a collaborator of org A «Solo alcuni» who was given <paramref name="grantedToCollaborator"/> only.
    /// </summary>
    private static HostScopeResolver Resolver(Guid? grantedToCollaborator = null)
    {
        var store = new Mock<IUserAuthorizationSnapshotStore>();
        store.Setup(s => s.GetAsync(Admin, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(new OrgMemberSnapshot(OrgA, OrgRole.Admin, OrgMemberStatus.Active)));
        store.Setup(s => s.GetAsync(Collaborator, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(new OrgMemberSnapshot(
                OrgA,
                OrgRole.Collaborator,
                OrgMemberStatus.Active,
                PropertyScope.Selected,
                grantedToCollaborator is { } granted ? new HashSet<Guid> { granted } : new HashSet<Guid>())));
        return new HostScopeResolver(store.Object, NullLogger<HostScopeResolver>.Instance);
    }

    private static UserAuthorizationSnapshot Snapshot(OrgMemberSnapshot member) =>
        new(Exists: true, IsActive: true, Role: UserRole.None, SupplierOrgId: null, Memberships: [], OrgId: member.OrgId, OrgMember: member);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"octorate-mapping-proof-{Guid.NewGuid()}").Options);

    private static async Task<Guid> SeedConnectionAsync(AppDbContext db, Guid orgId)
    {
        var connection = new OctorateConnection
        {
            OrgId = orgId,
            Environment = OctorateEnvironment.Test,
            Status = OctorateConnectionStatus.Connected,
        };
        db.OctorateConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection.Id;
    }

    private static async Task<Guid> SeedPropertyAsync(AppDbContext db, Guid orgId, string name)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = name,
            Address = "Via " + name + " 1",
            City = "Como",
            MaxGuests = 4,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static OctorateListingMapping Mapping(
        Guid orgId,
        Guid connectionId,
        string accommodationId,
        OctorateMappingStatus status,
        Guid? propertyId,
        string candidatesJson = OctorateListingMapping.EmptyJsonArray)
    {
        return new OctorateListingMapping
        {
            OrgId = orgId,
            ConnectionId = connectionId,
            OctorateAccommodationId = accommodationId,
            SnapshotName = "Listing " + accommodationId,
            Status = status,
            PropertyId = propertyId,
            CandidatePropertyIdsJson = candidatesJson,
            ConfirmedByUserId = status == OctorateMappingStatus.Confirmed ? Admin : null,
            ConfirmedAt = status == OctorateMappingStatus.Confirmed ? DateTime.UtcNow : null,
        };
    }
}
