using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the mapping of the listings (E02/F01/US02/T03): nothing is written to a property while its mapping is proposed or
/// ambiguous (data rule, OCT-04); a person confirms only to a property of the org within its reach, never takes over a listing whose
/// property it does not reach, and a confirmed listing is not moved to another property (permission rule, ADR-023-R4); the import never
/// creates anything on Octorate (data rule, ADR-027-R8).
/// </summary>
public class ChannelMappingProofTests
{
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a01");
    private static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c02");
    private const string Admin = "auth0|admin-a";
    private const string Collaborator = "auth0|collaborator-a";
    private const string Legacy = "auth0|legacy-manager-a";
    private static readonly IReadOnlySet<string> ManagerRoles = new HashSet<string> { "PropertyManager" };

    [Fact]
    public async Task ImportedCountry_UsesOfficialMunicipality_WithoutTrustingTheModel()
    {
        await using var db = NewDb();
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "5192", CantonCode = "TI", OfficialName = "Lugano" });
        await db.SaveChangesAsync();
        var ai = new Mock<IAiProvider>();
        ai.Setup(a => a.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("Lugano", 0, 0, AiModelTier.Economy, false));
        var listing = new OctorateProperty { Id = "1", Address = "Via Test 1, Lugano" };

        var country = await OctorateImportCountryResolver.ResolveAsync(db, listing, ai.Object, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(("CH", "Lugano"), country);
        ai.Verify(a => a.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        ai.Setup(a => a.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("Roma", 0, 0, AiModelTier.Economy, false));
        Assert.Equal((string.Empty, (string?)null), await OctorateImportCountryResolver.ResolveAsync(
            db, listing, ai.Object, NullLogger.Instance, CancellationToken.None));
        Assert.Equal((string.Empty, "Non presente"), await OctorateImportCountryResolver.ResolveAsync(db,
            new OctorateProperty { Id = "2", City = "Non presente" }, ai.Object, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task ImportedInactiveProperty_IsVisibleForReview_WithinItsHostScope()
    {
        await using var db = NewDb();
        var connection = await SeedConnectionAsync(db, OrgA);
        var imported = await SeedPropertyAsync(db, OrgA, "Importata");
        var unrelated = await SeedPropertyAsync(db, OrgA, "Altra inattiva");
        var otherOrg = await SeedPropertyAsync(db, OrgB, "Altra organizzazione");
        foreach (var property in await db.Properties.ToListAsync())
            property.IsActive = false;
        db.OctorateListingMappings.Add(Mapping(OrgA, connection, "1001", OctorateMappingStatus.Confirmed, imported));
        await db.SaveChangesAsync();

        var repository = new PropertyRepository(db);
        var visible = (await repository.GetByScopeAsync(new HostScope(OrgA))).Select(p => p.Id).ToArray();
        Assert.Contains(imported, visible);
        Assert.DoesNotContain(unrelated, visible);
        Assert.DoesNotContain(otherOrg, visible);
        Assert.NotNull(await repository.GetPropertyDetailAsync(imported));
        Assert.Null(await repository.GetPropertyDetailAsync(unrelated));
        var service = Service(db, new Mock<IOctorateApiClient>(MockBehavior.Strict).Object);
        Assert.True(await service.IsWriteBlockedAsync(imported, CancellationToken.None));
    }

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

        Assert.True(await service.IsWriteBlockedAsync(proposed, CancellationToken.None));
        Assert.True(await service.IsWriteBlockedAsync(candidate, CancellationToken.None));
        Assert.False(await service.IsWriteBlockedAsync(confirmed, CancellationToken.None));
        Assert.False(await service.IsWriteBlockedAsync(free, CancellationToken.None));
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
        var jobs = new Mock<IBackgroundJobClient>();
        var service = Service(db, client.Object, Resolver(granted), jobs: jobs.Object);

        var unreachable = await Assert.ThrowsAsync<NotFoundException>(
            () => service.ConfirmAsync(OrgA, "1001", mine, null, Collaborator, CancellationToken.None));
        Assert.Equal("octorate_property_not_found", unreachable.Code);

        var foreign = await Assert.ThrowsAsync<NotFoundException>(
            () => service.ConfirmAsync(OrgA, "1001", otherOrg, null, Admin, CancellationToken.None));
        Assert.Equal("octorate_property_not_found", foreign.Code);

        client.VerifyNoOtherCalls();
        jobs.VerifyNoOtherCalls();
        var untouched = await db.OctorateListingMappings.AsNoTracking().SingleAsync();
        Assert.Equal(OctorateMappingStatus.Proposed, untouched.Status);
        Assert.Null(untouched.ConfirmedByUserId);

        await service.ConfirmAsync(OrgA, "1001", mine, null, Admin, CancellationToken.None);
        var confirmed = await db.OctorateListingMappings.AsNoTracking().SingleAsync();
        Assert.Equal(OctorateMappingStatus.Confirmed, confirmed.Status);
        Assert.Equal(mine, confirmed.PropertyId);
        Assert.Equal(Admin, confirmed.ConfirmedByUserId);
        Assert.NotNull(confirmed.ConfirmedAt);
        Assert.Equal("956528", confirmed.OctorateProductId);
        jobs.Verify(j => j.Create(It.Is<Job>(job => job.Type == typeof(OctorateBookingImportJob)
            && job.Method.Name == nameof(OctorateBookingImportJob.ExecutePropertyAsync)
            && (Guid)job.Args[0] == OrgA && (Guid)job.Args[1] == mine), It.IsAny<EnqueuedState>()), Times.Once);
        Assert.Equal("""{"142":"BK-123456"}""", confirmed.ChannelIdsJson);

        db.OctorateListingMappings.Add(Mapping(OrgA, connection, "1002", OctorateMappingStatus.Proposed, mine));
        await db.SaveChangesAsync();
        var taken = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.ConfirmAsync(OrgA, "1002", mine, null, Admin, CancellationToken.None));
        Assert.Equal("octorate_mapping_property_taken", taken.Code);

        var takeover = await Assert.ThrowsAsync<NotFoundException>(
            () => service.ConfirmAsync(OrgA, "1001", granted, null, Collaborator, CancellationToken.None));
        Assert.Equal("octorate_property_not_found", takeover.Code);

        var moved = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.ConfirmAsync(OrgA, "1001", granted, null, Admin, CancellationToken.None));
        Assert.Equal("octorate_mapping_linked_to_other_property", moved.Code);
        var stays = await db.OctorateListingMappings.AsNoTracking().SingleAsync(m => m.OctorateAccommodationId == "1001");
        Assert.Equal(OctorateMappingStatus.Confirmed, stays.Status);
        Assert.Equal(mine, stays.PropertyId);
        client.Verify(c => c.GetRoomsAsync("1001", It.IsAny<CancellationToken>()), Times.Once);

        await service.RejectAsync(OrgA, "1001", Admin, CancellationToken.None);
        await service.ConfirmAsync(OrgA, "1001", granted, null, Admin, CancellationToken.None);
        var movedRow = await db.OctorateListingMappings.AsNoTracking().SingleAsync(m => m.OctorateAccommodationId == "1001");
        Assert.Equal(OctorateMappingStatus.Confirmed, movedRow.Status);
        Assert.Equal(granted, movedRow.PropertyId);

        var withoutRoles = await Assert.ThrowsAsync<NotFoundException>(
            () => service.SetRoomAsync(OrgA, "1001", "956528", Legacy, CancellationToken.None));
        Assert.Equal("octorate_property_not_found", withoutRoles.Code);
        await service.SetRoomAsync(OrgA, "1001", "956528", Legacy, CancellationToken.None, ManagerRoles);

        db.OctorateListingMappings.Add(Mapping(OrgA, connection, "1003", OctorateMappingStatus.Ambiguous, null, $"[\"{mine}\",\"{granted}\"]"));
        await db.SaveChangesAsync();
        var noProperty = await Assert.ThrowsAsync<NotFoundException>(
            () => service.RejectAsync(OrgA, "1003", Legacy, CancellationToken.None));
        Assert.Equal("octorate_mapping_not_found", noProperty.Code);
        await service.RejectAsync(OrgA, "1003", Legacy, CancellationToken.None, ManagerRoles);
        var rejected = await db.OctorateListingMappings.AsNoTracking().SingleAsync(m => m.OctorateAccommodationId == "1003");
        Assert.Equal(OctorateMappingStatus.Rejected, rejected.Status);
    }

    [Fact]
    public async Task ChannelMapping_ImportNeverCreatesOctorateProperty_Test()
    {
        await using var db = NewDb();
        var connection = await SeedConnectionAsync(db, OrgA);
        db.OctorateListingMappings.Add(Mapping(OrgA, connection, "1001", OctorateMappingStatus.Proposed, null));
        await db.SaveChangesAsync();

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

        client.Verify(c => c.GetPropertyAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetRoomsAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetConnectionsAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();

        properties.Verify(p => p.CreatePropertyAsync(It.IsAny<Property>()), Times.Once);
        var created = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
        Assert.Equal(OrgA, created.OrgId);
        Assert.Equal(Admin, created.OwnerId);
        Assert.False(created.IsActive);
        Assert.Equal("Casa del Lago", created.Name);
        Assert.Equal(3, created.MaxGuests);

        var mapping = await db.OctorateListingMappings.AsNoTracking().SingleAsync();
        Assert.Equal(OctorateMappingStatus.Confirmed, mapping.Status);
        Assert.Equal(propertyId, mapping.PropertyId);
        Assert.Equal("956528", mapping.OctorateProductId);
        Assert.Equal("""{"274":"AB-987654"}""", mapping.ChannelIdsJson);

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
        IInternalActivityService? activities = null,
        IBackgroundJobClient? jobs = null)
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
            NullLogger<ChannelMappingService>.Instance, jobs: jobs);
    }

    private static HostScopeResolver Resolver(Guid? grantedToCollaborator = null)
    {
        var store = new Mock<IUserAuthorizationSnapshotStore>();
        store.Setup(s => s.GetAsync(Legacy, It.IsAny<CancellationToken>())).ReturnsAsync(UserAuthorizationSnapshot.Missing);
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
