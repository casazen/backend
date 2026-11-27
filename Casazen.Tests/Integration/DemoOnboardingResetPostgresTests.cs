using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
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
/// E10/F01/US01/T04: the activation with Octorate the showcase writes for the demo org (a test connection with no token, the confirmed listings of
/// two houses on it, the bookings flow activated, a deferred part with its "To do"), and its reset, on a real PostgreSQL database where the foreign
/// keys of the connection are restrictive. A marker says what the showcase wrote, not what the row has become: the reset deletes the test connection,
/// the mappings on it and the deferred part only while they are still the invented ones, and never the real connection of the org, its tokens, its
/// real mappings or the host's own deferral.
/// </summary>
public class DemoOnboardingResetPostgresTests : IAsyncLifetime
{
    private static readonly DateTime At = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DemoOnboardingActivationTemplate Template = DemoShowcaseCatalog.OnboardingActivations[0];

    /// <summary>The kinds of rows the seed marks (the markers the reset drops).</summary>
    private static readonly string[] MarkedTypes =
    [
        TestDataEntities.Todo,
        TestDataEntities.OnboardingPartState,
        TestDataEntities.PropertyFlowActivation,
        TestDataEntities.OctorateListingMapping,
        TestDataEntities.OctorateConnection,
    ];

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(At));

    // One protection for every context of a test: the tokens one context writes are the ones the next reads.
    private readonly EphemeralDataProtectionProvider _protection = new();
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync("demoonbreset");

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Reset_DeletesTheInventedRows_EvenAMappingNobodyMarkedOnTheTestConnection_AndCanBeRepeated()
    {
        var world = await SeedHousesAsync();
        await CreateAsync(world);

        await using (var db = NewDb())
        {
            // What a job of the connector that found no token may write on the test connection, with nobody's marker: the foreign keys would
            // make the reset fail, and no real row can be there.
            var connectionId = await db.OctorateConnections.IgnoreQueryFilters().Where(c => c.OrgId == world.OrgId).Select(c => c.Id).SingleAsync();
            db.OctorateListingMappings.Add(new OctorateListingMapping { OrgId = world.OrgId, ConnectionId = connectionId, OctorateAccommodationId = "TEST-UNMARKED" });
            db.OtaMessageSyncStates.Add(new OtaMessageSyncState { OrgId = world.OrgId, ConnectionId = connectionId, OctorateAccommodationId = "TEST-ACC-ATTIVA" });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            // The connection, the two marked listings and the unmarked one, the bookings flow of the first house, the deferred part and its "To do".
            Assert.Equal(new Counts(1, 3, 1, 1, 1, 6), await CountsAsync(db, world.OrgId));
        }

        await ResetTwiceAsync(world);

        await using (var db = NewDb())
        {
            Assert.Equal(new Counts(0, 0, 0, 0, 0, 0), await CountsAsync(db, world.OrgId));
            Assert.Equal(0, await db.OtaMessageSyncStates.IgnoreQueryFilters().CountAsync(s => s.OrgId == world.OrgId));

            // The houses and their markers are the reset of the houses' business.
            Assert.Equal(3, await db.Properties.IgnoreQueryFilters().CountAsync(p => p.OrgId == world.OrgId));
            Assert.Equal(3, await db.TestDataMarkers.IgnoreQueryFilters().CountAsync(m => m.OrgId == world.OrgId && m.EntityType == TestDataEntities.Property));
        }

        // The seed writes it all again.
        await CreateAsync(world);
        await using (var db = NewDb())
        {
            Assert.Equal(new Counts(1, 2, 1, 1, 1, 6), await CountsAsync(db, world.OrgId));
        }
    }

    [PostgresFact]
    public async Task Seed_WithAConnectionOfTheOrgsOwn_WritesNoInventedOne_AndTheResetNeverTouchesIt()
    {
        var world = await SeedHousesAsync();
        await using (var db = NewDb())
        {
            var real = HostScopeScenario.NewProperty(world.OrgId, world.OwnerId, "Casa Vera");
            var connection = RealConnection(world.OrgId);
            db.Properties.Add(real);
            db.OctorateConnections.Add(connection);
            db.OctorateListingMappings.Add(RealMapping(world.OrgId, connection.Id, real.Id));
            await db.SaveChangesAsync();
        }

        // No second connection and no invented listing next to the real ones; the activation and the deferred part are written anyway.
        await CreateAsync(world);
        await using (var db = NewDb())
        {
            var counts = await CountsAsync(db, world.OrgId);
            Assert.Equal((1, 1, 1, 1, 1), (counts.Connections, counts.Mappings, counts.Activations, counts.PartStates, counts.Todos));
        }

        await ResetTwiceAsync(world);

        await using (var db = NewDb())
        {
            await AssertTheRealConnectionIsLeftAloneAsync(db, world.OrgId);
            var counts = await CountsAsync(db, world.OrgId);
            Assert.Equal(new Counts(1, 1, 0, 0, 0, 0), counts);
        }
    }

    [PostgresFact]
    public async Task Reset_AfterARealConsentTookTheTestConnectionOver_KeepsItsTokensItsMappingsAndTheHostsOwnDeferral()
    {
        var world = await SeedHousesAsync();
        await CreateAsync(world);

        // A real consent reuses the one connection of the org and the environment: its account and its tokens go into the marked row. The host then
        // confirms a real listing on it and puts the very same part off again (the one row of the part is rewritten, its "To do" replaced).
        await using (var db = NewDb())
        {
            var real = HostScopeScenario.NewProperty(world.OrgId, world.OwnerId, "Casa Vera");
            db.Properties.Add(real);
            var connection = await db.OctorateConnections.IgnoreQueryFilters().SingleAsync(c => c.OrgId == world.OrgId);
            connection.OctorateAccountId = "REAL-NET";
            connection.EncryptedRefreshToken = "real-refresh";
            connection.EncryptedAccessToken = "real-access";
            db.OctorateListingMappings.Add(RealMapping(world.OrgId, connection.Id, real.Id));
            await db.SaveChangesAsync();
        }

        Guid hostTodoId;
        await using (var db = NewDb())
        {
            var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, _clock);
            var plan = new OnboardingPlanService(db, [new OpenListingsEvaluator()], activities, _clock, NullLogger<OnboardingPlanService>.Instance);
            var change = await plan.DeferAsync(world.OrgId, world.OwnerId, OnboardingPartKey.Listings, At.AddDays(10), Ct);
            hostTodoId = change.TodoId!.Value;
        }

        await ResetTwiceAsync(world);

        await using (var db = NewDb())
        {
            // The real connection with its tokens, and the real listing: the two invented ones went, the real one did not.
            await AssertTheRealConnectionIsLeftAloneAsync(db, world.OrgId);

            // The host's own deferral stays with the "To do" the host's call raised (open, pointing at the row); the invented "To do" went.
            var part = await db.OnboardingPartStates.IgnoreQueryFilters().SingleAsync(s => s.OrgId == world.OrgId);
            Assert.Equal(
                (OnboardingPartKey.Listings, OnboardingPartStatus.Deferred, (DateTime?)At.AddDays(10), (Guid?)hostTodoId),
                (part.PartKey, part.Status, part.RemindAt, part.ActivityId));
            var todo = await db.InternalActivities.IgnoreQueryFilters().SingleAsync(a => a.OrgId == world.OrgId && a.Action != null);
            Assert.Equal((hostTodoId, (Guid?)part.Id, (DateTime?)null), (todo.Id, todo.EntityId, todo.CompletedAt));

            // The invented activation of the first house went with the rest; no marker of the seed is left.
            Assert.Equal(new Counts(1, 1, 0, 1, 1, 0), await CountsAsync(db, world.OrgId));
        }

        // The seed after the reset finds the org's own connection and part: it writes neither again.
        await CreateAsync(world);
        await using (var db = NewDb())
        {
            Assert.Equal(new Counts(1, 1, 1, 1, 1, 1), await CountsAsync(db, world.OrgId));
        }
    }

    private static async Task AssertTheRealConnectionIsLeftAloneAsync(AppDbContext db, Guid orgId)
    {
        var connection = await db.OctorateConnections.IgnoreQueryFilters().SingleAsync(c => c.OrgId == orgId);
        Assert.Equal(
            ("REAL-NET", "real-refresh", "real-access", OctorateConnectionStatus.Connected),
            (connection.OctorateAccountId, connection.EncryptedRefreshToken, connection.EncryptedAccessToken, connection.Status));
        var mapping = await db.OctorateListingMappings.IgnoreQueryFilters().SingleAsync(m => m.OrgId == orgId);
        Assert.Equal(("REAL-ACC", OctorateMappingStatus.Confirmed, connection.Id), (mapping.OctorateAccommodationId, mapping.Status, mapping.ConnectionId));
    }

    private static OctorateConnection RealConnection(Guid orgId) => new()
    {
        OrgId = orgId,
        Environment = OctorateEnvironment.Test,
        Status = OctorateConnectionStatus.Connected,
        OctorateAccountId = "REAL-NET",
        EncryptedRefreshToken = "real-refresh",
        EncryptedAccessToken = "real-access",
    };

    private static OctorateListingMapping RealMapping(Guid orgId, Guid connectionId, Guid propertyId) => new()
    {
        OrgId = orgId,
        ConnectionId = connectionId,
        OctorateAccommodationId = "REAL-ACC",
        PropertyId = propertyId,
        Status = OctorateMappingStatus.Confirmed,
        MatchScore = 100,
    };

    private AppDbContext NewDb() => new(_database!.CreateOptions(), tenantContext: null, _protection);

    private DemoEnvironmentService Service(AppDbContext db) => new(
        db,
        Mock.Of<IFileStorage>(),
        Mock.Of<IPropertyComplianceStatusService>(),
        Options.Create(new ComplianceOptions()),
        Mock.Of<IDemoEnvironmentGate>(),
        NullLogger<DemoEnvironmentService>.Instance,
        _clock,
        Mock.Of<IServiceScopeFactory>(),
        Mock.Of<ICrmContactIndex>(),
        Mock.Of<IFeatureFlags>(),
        Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Development));

    /// <summary>The seed of the activation, as the reset runs it after the houses.</summary>
    private async Task CreateAsync(World world)
    {
        await using var db = NewDb();
        await Service(db).CreateOnboardingActivationAsync(world.OrgId, world.OwnerId, Template, Ct);
    }

    /// <summary>The removal of the activation, as the reset runs it, twice: the second finds nothing and changes nothing.</summary>
    private async Task ResetTwiceAsync(World world)
    {
        await using var db = NewDb();
        var service = Service(db);
        await service.RemoveShowcaseOnboardingAsync(world.OrgId, Ct);
        await service.RemoveShowcaseOnboardingAsync(world.OrgId, Ct);
    }

    /// <summary>An org with its owner and the three houses of the template (the houses the showcase just made), each with its marker.</summary>
    private async Task<World> SeedHousesAsync()
    {
        var kit = new OrgInvitationTestKit(() => _database!.CreateContext());
        var (org, owner) = await kit.SeedOwnerOrgAsync($"auth0|onb-{Guid.NewGuid():N}");

        await using var db = NewDb();
        foreach (var house in Template.Properties)
        {
            var name = DemoShowcaseCatalog.Properties.Single(p => p.Key == house.PropertyKey).Name;
            var property = HostScopeScenario.NewProperty(org.Id, owner.Id, name);
            db.Properties.Add(property);
            db.TestDataMarkers.Add(new TestDataMarker
            {
                OrgId = org.Id,
                EntityType = TestDataEntities.Property,
                EntityId = property.Id,
                Origin = TestDataOrigins.Showcase,
                TemplateKey = house.PropertyKey,
            });
        }

        await db.SaveChangesAsync();
        return new World(org.Id, owner.Id);
    }

    private static async Task<Counts> CountsAsync(AppDbContext db, Guid orgId) => new(
        await db.OctorateConnections.IgnoreQueryFilters().CountAsync(c => c.OrgId == orgId),
        await db.OctorateListingMappings.IgnoreQueryFilters().CountAsync(m => m.OrgId == orgId),
        await db.PropertyFlowActivations.IgnoreQueryFilters().CountAsync(a => a.OrgId == orgId),
        await db.OnboardingPartStates.IgnoreQueryFilters().CountAsync(s => s.OrgId == orgId),
        await db.InternalActivities.IgnoreQueryFilters().CountAsync(a => a.OrgId == orgId && a.Action != null),
        await db.TestDataMarkers.IgnoreQueryFilters().CountAsync(m => m.OrgId == orgId && MarkedTypes.Contains(m.EntityType)));

    private sealed record World(Guid OrgId, string OwnerId);

    private sealed record Counts(int Connections, int Mappings, int Activations, int PartStates, int Todos, int Markers);

    /// <summary>The part «Listings» as an org with nothing done sees it: provided, not complete.</summary>
    private sealed class OpenListingsEvaluator : IOnboardingPartEvaluator
    {
        public OnboardingPartKey Part => OnboardingPartKey.Listings;

        public int Order => OnboardingPartEvaluatorOrders.Evaluator;

        public Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct) =>
            Task.FromResult(new PartFacts(true, 0, 1, OnboardingPartReasons.ChannelsMissing));
    }
}
