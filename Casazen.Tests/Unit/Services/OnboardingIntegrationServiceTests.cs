using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The integration settings of the activation (E10/F01/US02/T01, NF-108): the proofs of its privacy and permission rules, and the rules of the
/// overview and of the reopen, on EF InMemory and the world of <see cref="HostScopeScenario"/> (an org with a property given to the
/// collaborator «Solo alcuni», one not given, and another org).
/// </summary>
public class OnboardingIntegrationServiceTests : IAsyncLifetime
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Proof (privacy, ADR-023-R2 and R13): the overview serializes no token and no ciphertext of the <c>OctorateConnection</c>. The connection
    /// holds an access and a refresh token; neither the clear value, nor its tail, nor the Data Protection payload the column keeps shows in the
    /// overview (as the service answers it and as the API serializes it), and no type of the answer has a member that could carry one.
    /// </summary>
    [Fact]
    public async Task OnboardingIntegration_NeverExposesTokens_Test()
    {
        const string access = "oct-access-7c1f0e2a9b4d4f6e8a1b3c5d7e9f0a2b6c8d";
        const string refresh = "oct-refresh-4e6a8c0b2d4f6a8c0e2b4d6f8a0c2e4f6a8c";
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            Provider);
        var world = await HostScopeScenario.SeedAsync(db);
        db.OctorateConnections.Add(new OctorateConnection
        {
            OrgId = world.OrgId,
            Environment = OctorateEnvironment.Test,
            Status = OctorateConnectionStatus.Error,
            OctorateAccountId = "acc-1",
            EncryptedAccessToken = access,
            EncryptedRefreshToken = refresh,
            TokenExpiresAt = HostScopeScenario.Now.UtcDateTime.AddHours(1),
            LastSyncedAt = HostScopeScenario.Now.UtcDateTime.AddHours(-2),
        });
        await db.SaveChangesAsync();

        // What the column keeps for each token: the payload of the Octorate purpose, so the proof covers the ciphertext too.
        var entity = db.Model.FindEntityType(typeof(OctorateConnection))!;
        var secrets = new List<string> { access, refresh, access[^16..], refresh[^16..] };
        foreach (var (property, token) in new[]
                 {
                     (nameof(OctorateConnection.EncryptedAccessToken), access),
                     (nameof(OctorateConnection.EncryptedRefreshToken), refresh),
                 })
        {
            var converter = Assert.IsType<EncryptedStringConverter>(entity.FindProperty(property)!.GetValueConverter());
            secrets.Add((string)converter.ConvertToProvider(token)!);
        }

        var overview = await Build(db).Integration.GetOverviewAsync(world.OrgId, world.OrgWide, Ct);
        var reopen = await Build(db).Integration.ReopenPartAsync(world.OrgId, world.OwnerUserId, OnboardingPartKey.OctorateConnection, Ct);

        Assert.True(overview.Connection.ReconnectRequired);
        foreach (var answer in new[]
                 {
                     JsonSerializer.Serialize(overview, Web),
                     JsonSerializer.Serialize(overview.ToDto(), Web),
                     JsonSerializer.Serialize(reopen, Web),
                     JsonSerializer.Serialize(reopen.ToDto(), Web),
                 })
        {
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, answer, StringComparison.Ordinal));
            Assert.DoesNotContain(EncryptedColumns.ProtectedPayloadPrefix, answer, StringComparison.Ordinal);
        }

        // Nothing in the types of the answer could carry one either.
        var carriers = MemberNamesOf(typeof(OnboardingIntegrationOverview), typeof(Casazen.Web.DTOs.Onboarding.OnboardingIntegrationDto), typeof(OnboardingPartReopen))
            .Where(name => new[] { "token", "cipher", "encrypted", "secret", "refresh" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(carriers.Count == 0, "A member of the integration settings could carry a token: " + string.Join(", ", carriers));
    }

    /// <summary>
    /// Proof (permission): the overview and the reopen are under the org policy <see cref="CasazenPolicies.OrgBillingAdmin"/> and nothing else
    /// opens them: no <c>[AllowAnonymous]</c>, no other routed action on the controller, behind <c>Features:OctorateOnboarding</c>; and the real
    /// registered policy refuses a collaborator of the org (a DB membership, no Staff claim) and a property manager, while the owner passes (the
    /// positive control).
    /// </summary>
    [Fact]
    public async Task OnboardingIntegration_RequiresOrgAdmin_Test()
    {
        string[] names = [nameof(OnboardingIntegrationController.GetOverview), nameof(OnboardingIntegrationController.Reopen)];
        var actions = OrgBillingAdminActions.All.Where(a => a.Controller == typeof(OnboardingIntegrationController)).ToList();
        var routed = typeof(OnboardingIntegrationController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any())
            .Select(m => m.Name);

        Assert.Equal(names.Order(StringComparer.Ordinal), routed.Order(StringComparer.Ordinal));
        Assert.Equal(names.Order(StringComparer.Ordinal), actions.Select(a => a.Method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["GET", "POST"], actions.Select(a => a.HttpMethod).Order(StringComparer.Ordinal));
        Assert.All(actions, action =>
        {
            Assert.False(action.IsAnonymous);
            Assert.Equal([CasazenPolicies.OrgBillingAdmin], action.Policies);
            Assert.Equal([FeatureFlags.OctorateOnboarding], action.FeatureGates);
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.OrgBillingAdmin);
        Assert.NotNull(policy);

        var collaborator = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.None, ["short-rent/bk09_collaborator"]));
        var manager = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.PropertyManager, ["short-rent/property_manager"]));
        var owner = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.PropertyOwner, ["short-rent/property_owner"]));

        var asCollaborator = new AuthorizationHandlerContext(policy.Requirements, Principal(), resource: null);
        var asManager = new AuthorizationHandlerContext(policy.Requirements, Principal("PropertyManager"), resource: null);
        var asOwner = new AuthorizationHandlerContext(policy.Requirements, Principal("PropertyOwner"), resource: null);
        await collaborator.HandleAsync(asCollaborator);
        await manager.HandleAsync(asManager);
        await owner.HandleAsync(asOwner);

        Assert.False(asCollaborator.HasSucceeded, "A collaborator of the org passed the policy of the integration settings.");
        Assert.False(asManager.HasSucceeded, "A property manager passed the policy of the integration settings.");
        Assert.True(asOwner.HasSucceeded, "The owner of the org does not pass the policy of the integration settings.");
    }

    /// <summary>
    /// Proof (data, permission): the overview speaks about the caller's org only, and over what the caller reaches (AM-03). The collaborator «Solo
    /// alcuni» counts the senders, the channel states and the parts of the plan over its one property; the owner over the two houses of the org; the other org's
    /// connection, sender and channel states are in neither, and in turn the other org sees only its own. A scope of another org is refused.
    /// </summary>
    [Fact]
    public async Task GetOverview_CountsOnlyTheOrgOfTheCaller_AndThePropertiesTheScopeReaches()
    {
        var syncedAt = new DateTime(2026, 10, 8, 6, 30, 0, DateTimeKind.Utc);
        _db.OctorateConnections.AddRange(
            new OctorateConnection { OrgId = _world.OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Error, LastSyncedAt = syncedAt },
            new OctorateConnection { OrgId = _world.OtherOrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected });
        _db.PropertyComplianceSenders.AddRange(
            Sender(_world.Granted, ComplianceSenderType.ChannelManager),
            Sender(_world.Hidden, ComplianceSenderType.Manual),
            Sender(_world.OtherOrgProperty, ComplianceSenderType.CasaZen));
        _db.ListingChannelStates.AddRange(
            Channel(_world.Granted, "booking_xml", ChannelPublishStatus.Published),
            Channel(_world.Granted, "airbnb_xml", ChannelPublishStatus.ToFix),
            Channel(_world.Hidden, "booking_xml", ChannelPublishStatus.Draft),
            Channel(_world.OtherOrgProperty, "booking_xml", ChannelPublishStatus.Published),
            Channel(_world.OtherOrgProperty, "airbnb_xml", ChannelPublishStatus.Published),
            Channel(_world.OtherOrgProperty, "expedia", ChannelPublishStatus.ManagedOnChannel));
        await _db.SaveChangesAsync();
        var service = Build(_db).Integration;

        var restricted = await service.GetOverviewAsync(_world.OrgId, _world.Restricted, Ct);
        var orgWide = await service.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);
        var other = await service.GetOverviewAsync(_world.OtherOrgId, new HostScope(_world.OtherOrgId), Ct);

        Assert.Equal((OctorateConnectionStatus.Error, true, syncedAt), (orgWide.Connection.Status, orgWide.Connection.ReconnectRequired, orgWide.Connection.LastSyncedAt));
        Assert.Equal(orgWide.Connection, restricted.Connection);
        Assert.Equal((OctorateConnectionStatus.Connected, false), (other.Connection.Status, other.Connection.ReconnectRequired));

        Assert.Equal(new OnboardingSenderCounts(1, 0, 0, 0), restricted.Senders);
        Assert.Equal(new OnboardingSenderCounts(1, 0, 1, 0), orgWide.Senders);
        Assert.Equal(new OnboardingSenderCounts(0, 1, 0, 0), other.Senders);

        Assert.Equal([(ChannelPublishStatus.Published, 1), (ChannelPublishStatus.ToFix, 1)], Counted(restricted));
        Assert.Equal([(ChannelPublishStatus.Draft, 1), (ChannelPublishStatus.Published, 1), (ChannelPublishStatus.ToFix, 1)], Counted(orgWide));
        Assert.Equal([(ChannelPublishStatus.Published, 2), (ChannelPublishStatus.ManagedOnChannel, 1)], Counted(other));

        // The parts follow the reach too: the plan is read over the same scope, so the evaluators count the one house of the collaborator and the
        // two of the owner (a plan read over the whole org for everybody would show 2 of 2 to the collaborator).
        Assert.Equal(11, orgWide.Parts.Count);
        Assert.Equal(11, restricted.Parts.Count);
        Assert.Equal((Done: 1, Total: 1), ProgressOf(restricted, OnboardingPartKey.PricesRules));
        Assert.Equal((Done: 2, Total: 2), ProgressOf(orgWide, OnboardingPartKey.PricesRules));
        Assert.Equal((Done: 1, Total: 1), ProgressOf(restricted, OnboardingPartKey.Channels));
        Assert.Equal((Done: 2, Total: 2), ProgressOf(orgWide, OnboardingPartKey.Channels));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetOverviewAsync(_world.OtherOrgId, _world.OrgWide, Ct));
    }

    /// <summary>
    /// The senders are counted from the stored choices of E03, over the short-rent, active properties the plan counts: a house with no stored
    /// choice is <c>notSet</c>; a house outside Italy has no sender to choose, a long-rent one and an inactive one are not the plan's, so none of
    /// them is in any count (even with a stored row).
    /// </summary>
    [Fact]
    public async Task GetOverview_SenderCounts_CountTheStoredChoices_OfTheShortRentActiveItalianHouses()
    {
        var free = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, "Casa Libera");
        var abroad = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, "Chalet");
        abroad.Country = "CH";
        var longRent = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, "Appartamento");
        longRent.RentalMode = RentalMode.Long;
        var inactive = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, "Casa Chiusa");
        inactive.IsActive = false;
        _db.Properties.AddRange(free, abroad, longRent, inactive);
        _db.PropertyComplianceSenders.AddRange(
            Sender(_world.Granted, ComplianceSenderType.ChannelManager),
            Sender(_world.Hidden, ComplianceSenderType.CasaZen),
            Sender(abroad, ComplianceSenderType.Manual),
            Sender(longRent, ComplianceSenderType.Manual),
            Sender(inactive, ComplianceSenderType.Manual));
        await _db.SaveChangesAsync();

        var overview = await Build(_db).Integration.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);

        Assert.Equal(new OnboardingSenderCounts(ChannelManager: 1, CasaZen: 1, Manual: 0, NotSet: 1), overview.Senders);
    }

    /// <summary>
    /// The channel states are counted over the same houses, and every publication status is listed in the order of the enum, with zero when none
    /// is in it: the client never has to guess a missing status.
    /// </summary>
    [Fact]
    public async Task GetOverview_Channels_ListEveryStatusInOrder_AndSkipTheHousesThePlanDoesNotCount()
    {
        var longRent = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, "Appartamento");
        longRent.RentalMode = RentalMode.Long;
        var inactive = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, "Casa Chiusa");
        inactive.IsActive = false;
        _db.Properties.AddRange(longRent, inactive);
        _db.ListingChannelStates.AddRange(
            Channel(_world.Granted, "booking_xml", ChannelPublishStatus.Published),
            Channel(_world.Granted, "airbnb_xml", ChannelPublishStatus.Sending),
            Channel(_world.Hidden, "booking_xml", ChannelPublishStatus.Published),
            Channel(_world.Hidden, "expedia", ChannelPublishStatus.Accepted),
            Channel(longRent, "booking_xml", ChannelPublishStatus.Published),
            Channel(inactive, "booking_xml", ChannelPublishStatus.ToFix));
        await _db.SaveChangesAsync();

        var overview = await Build(_db).Integration.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);

        Assert.Equal(
            Enum.GetValues<ChannelPublishStatus>().OrderBy(status => (int)status),
            overview.Channels.Select(count => count.Status));
        Assert.Equal(
            [
                (ChannelPublishStatus.Draft, 0),
                (ChannelPublishStatus.Sending, 1),
                (ChannelPublishStatus.Accepted, 1),
                (ChannelPublishStatus.Published, 2),
                (ChannelPublishStatus.ToFix, 0),
                (ChannelPublishStatus.ManagedOnChannel, 0),
            ],
            overview.Channels.Select(count => (count.Status, count.Count)));
    }

    /// <summary>
    /// AC-NF108-4: a connection that is disconnected or in error says <c>ReconnectRequired</c>, and the part «Octorate connection» of the plan
    /// carries the reason <c>connection_lost</c> (also for houses that declared no inventory scenario: a lost connection stops every scenario);
    /// a connected one needs nothing.
    /// </summary>
    [Theory]
    [InlineData(OctorateConnectionStatus.Connected, false)]
    [InlineData(OctorateConnectionStatus.Disconnected, true)]
    [InlineData(OctorateConnectionStatus.Error, true)]
    public async Task GetOverview_Connection_SaysReconnectRequired_OnlyWhenDisconnectedOrInError(OctorateConnectionStatus status, bool reconnectRequired)
    {
        var syncedAt = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        _db.OctorateConnections.Add(new OctorateConnection { OrgId = _world.OrgId, Environment = OctorateEnvironment.Test, Status = status, LastSyncedAt = syncedAt });
        await _db.SaveChangesAsync();

        var overview = await Build(_db).Integration.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);

        Assert.Equal(new OnboardingConnectionHealth(status, OctorateEnvironment.Test, syncedAt, reconnectRequired), overview.Connection);
        var part = overview.Parts.Single(p => p.Plan.Key == OnboardingPartKey.OctorateConnection).Plan;
        Assert.NotEqual(OnboardingPartStates.Done, part.State);
        Assert.Equal(reconnectRequired ? OnboardingPartReasons.ConnectionLost : OnboardingPartReasons.InventoryScenarioMissing, part.Reason);
    }

    /// <summary>
    /// A connection never made has no status and needs no reconnection (it is a connect, not a reconnect); the connection of the other Octorate
    /// environment is not this one's: only the row of this environment is reported.
    /// </summary>
    [Fact]
    public async Task GetOverview_Connection_NeverMade_HasNoStatus_AndOnlyThisEnvironmentCounts()
    {
        var service = Build(_db).Integration;

        var never = await service.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);

        Assert.Equal(new OnboardingConnectionHealth(null, OctorateEnvironment.Test, null, false), never.Connection);

        _db.OctorateConnections.AddRange(
            new OctorateConnection { OrgId = _world.OrgId, Environment = OctorateEnvironment.Production, Status = OctorateConnectionStatus.Connected },
            new OctorateConnection { OrgId = _world.OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Disconnected });
        await _db.SaveChangesAsync();

        var thisEnvironment = await service.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);

        Assert.Equal((OctorateConnectionStatus.Disconnected, OctorateEnvironment.Test, true), (thisEnvironment.Connection.Status, thisEnvironment.Connection.Environment, thisEnvironment.Connection.ReconnectRequired));
    }

    /// <summary>
    /// <c>reopenable</c> is false only for a part nobody provides (there is no wizard to go back to); a part that is done, deferred, open or
    /// blocked can be reopened (AC-NF108-1: any part).
    /// </summary>
    [Fact]
    public async Task GetOverview_Parts_AreReopenable_ExceptThoseNobodyProvides()
    {
        var (service, plan) = Build(_db);
        await plan.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Listings, null, Ct);

        var overview = await service.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);

        var parts = overview.Parts.ToDictionary(p => p.Plan.Key);
        Assert.Equal(OnboardingPartCatalog.Parts.Select(p => p.Key), overview.Parts.Select(p => p.Plan.Key));
        Assert.Equal(OnboardingPartStates.Deferred, parts[OnboardingPartKey.Listings].Plan.State);
        Assert.Equal(OnboardingPartStates.Done, parts[OnboardingPartKey.PricesRules].Plan.State);
        Assert.Equal(OnboardingPartStates.Blocked, parts[OnboardingPartKey.Activation].Plan.State);
        foreach (var key in new[] { OnboardingPartKey.MessagesAi, OnboardingPartKey.FiscalDocuments, OnboardingPartKey.Partners })
        {
            Assert.Equal(OnboardingPartStates.NotAvailable, parts[key].Plan.State);
            Assert.False(parts[key].Reopenable);
        }

        Assert.All(overview.Parts.Where(p => p.Plan.State != OnboardingPartStates.NotAvailable), p => Assert.True(p.Reopenable));
        Assert.Equal(8, overview.Parts.Count(p => p.Reopenable));
    }

    /// <summary>The API shape the settings page reads: the names of the members, the enums as text, every status listed.</summary>
    [Fact]
    public async Task GetOverview_Dto_HasTheShapeOfTheContract()
    {
        _db.OctorateConnections.Add(new OctorateConnection { OrgId = _world.OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Error });
        await _db.SaveChangesAsync();
        var (service, _) = Build(_db);

        var overview = await service.GetOverviewAsync(_world.OrgId, _world.OrgWide, Ct);
        var reopen = await service.ReopenPartAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Compliance, Ct);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(overview.ToDto(), Web));
        var root = document.RootElement;
        Assert.Equal(["channels", "connection", "parts", "senders"], Names(root));
        Assert.Equal(["environment", "lastSyncedAt", "reconnectRequired", "status"], Names(root.GetProperty("connection")));
        Assert.Equal("Error", root.GetProperty("connection").GetProperty("status").GetString());
        Assert.Equal(["casaZen", "channelManager", "manual", "notSet"], Names(root.GetProperty("senders")));
        Assert.Equal(6, root.GetProperty("channels").GetArrayLength());
        Assert.Equal(["count", "status"], Names(root.GetProperty("channels")[0]));
        Assert.Equal("Draft", root.GetProperty("channels")[0].GetProperty("status").GetString());
        Assert.Equal(11, root.GetProperty("parts").GetArrayLength());
        Assert.Equal(
            ["assist", "code", "deferrable", "done", "launchTarget", "needs", "order", "reason", "remindAt", "reopenable", "requires", "state", "total"],
            Names(root.GetProperty("parts")[0]));

        using var reopened = JsonDocument.Parse(JsonSerializer.Serialize(reopen.ToDto(), Web));
        Assert.Equal(["code", "focus", "launchTarget"], Names(reopened.RootElement));
        Assert.Equal("sender", reopened.RootElement.GetProperty("focus").GetString());
    }

    /// <summary>
    /// Reopening a part that was put off clears the deferral (the stored intent is open again, with no reminder) and closes its "To do"
    /// (AC-NF108-2), and says where to continue: the compliance wizard, on the choice of the sender. The same part of another org stays put off,
    /// with its "To do" open.
    /// </summary>
    [Fact]
    public async Task Reopen_ADeferredPart_ClearsTheDeferral_ClosesItsTodo_AndSaysWhereToContinue()
    {
        var (service, plan) = Build(_db);
        var deferred = await plan.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Compliance, null, Ct);
        var otherDeferred = await plan.DeferAsync(_world.OtherOrgId, "auth0|altro-titolare", OnboardingPartKey.Compliance, null, Ct);
        Assert.Equal(OnboardingPartStates.Deferred, (await StateOfAsync(plan, OnboardingPartKey.Compliance)));

        var answer = await service.ReopenPartAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Compliance, Ct);

        Assert.Equal(new OnboardingPartReopen(OnboardingPartKey.Compliance, "compliance", OnboardingLaunchTargets.Compliance, OnboardingReopenFocus.Sender), answer);
        // No longer deferred: it is back to the state the facts give it (blocked here, because the part it requires, the properties, is not done).
        Assert.Equal(OnboardingPartStates.Blocked, await StateOfAsync(plan, OnboardingPartKey.Compliance));
        Assert.NotNull((await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == deferred.TodoId)).CompletedAt);
        var rows = await _db.OnboardingPartStates.AsNoTracking().ToListAsync();
        var mine = Assert.Single(rows, r => r.OrgId == _world.OrgId);
        Assert.Equal((OnboardingPartStatus.Open, (DateTime?)null, (Guid?)null), (mine.Status, mine.RemindAt, mine.ActivityId));

        var others = Assert.Single(rows, r => r.OrgId == _world.OtherOrgId);
        Assert.Equal((OnboardingPartStatus.Deferred, otherDeferred.TodoId), (others.Status, others.ActivityId));
        Assert.Null((await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == otherDeferred.TodoId)).CompletedAt);
    }

    /// <summary>
    /// Every part the org can do answers the launch target of the catalog (add a channel is <c>channels</c>, add properties <c>mapping</c>,
    /// reconnect <c>connect</c>), and the focus <c>sender</c> only for the compliance wizard. A part never put off is reopened without writing
    /// anything.
    /// </summary>
    [Fact]
    public async Task Reopen_AnswersTheLaunchTargetOfThePart_AndTheSenderFocusOnlyForCompliance()
    {
        var service = Build(_db).Integration;
        var provided = OnboardingPartCatalog.Parts
            .Where(p => p.Key is not (OnboardingPartKey.MessagesAi or OnboardingPartKey.FiscalDocuments or OnboardingPartKey.Partners))
            .ToList();

        var answers = new List<OnboardingPartReopen>();
        foreach (var definition in provided)
            answers.Add(await service.ReopenPartAsync(_world.OrgId, _world.OwnerUserId, definition.Key, Ct));

        Assert.Equal(provided.Select(p => (p.Key, p.Code, p.LaunchTarget)), answers.Select(a => (a.Key, a.Code, a.LaunchTarget)));
        Assert.Equal("channels", answers.Single(a => a.Key == OnboardingPartKey.Channels).LaunchTarget);
        Assert.Equal("mapping", answers.Single(a => a.Key == OnboardingPartKey.Properties).LaunchTarget);
        Assert.Equal("connect", answers.Single(a => a.Key == OnboardingPartKey.OctorateConnection).LaunchTarget);
        var focused = Assert.Single(answers, a => a.Focus is not null);
        Assert.Equal((OnboardingPartKey.Compliance, "sender"), (focused.Key, focused.Focus));

        Assert.Empty(await _db.OnboardingPartStates.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InternalActivities.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A reopen never changes data: the parts stand put off or left half way (so the reopen does its one write, the deferral and its "To do"),
    /// and the senders, the channel states, the connection and the houses are the same after reopening the sender, the channels, the properties
    /// and the connection. The sender changes only through the sender switch of E03 (no second path here).
    /// </summary>
    [Fact]
    public async Task Reopen_ChangesNoSenderChannelConnectionOrProperty()
    {
        _db.OctorateConnections.Add(new OctorateConnection
        {
            OrgId = _world.OrgId,
            Environment = OctorateEnvironment.Test,
            Status = OctorateConnectionStatus.Disconnected,
            EncryptedRefreshToken = "kept-refresh",
        });
        _db.PropertyComplianceSenders.AddRange(
            Sender(_world.Granted, ComplianceSenderType.ChannelManager),
            Sender(_world.Hidden, ComplianceSenderType.Manual));
        _db.ListingChannelStates.Add(Channel(_world.Granted, "booking_xml", ChannelPublishStatus.Published));
        await _db.SaveChangesAsync();
        var (service, plan) = Build(_db);

        // Every part is put off first (the ones nothing else needs) or left half way (the others), so each one has its stored intent and an open
        // "To do": the reopen then takes the one path that writes, and the rows it must not touch still do not change.
        OnboardingPartKey[] parts = [OnboardingPartKey.Compliance, OnboardingPartKey.Channels, OnboardingPartKey.Properties, OnboardingPartKey.OctorateConnection];
        foreach (var key in parts)
        {
            if (OnboardingPartCatalog.IsDeferrable(key))
                await plan.DeferAsync(_world.OrgId, _world.OwnerUserId, key, null, Ct);
            else
                await plan.ExitAsync(_world.OrgId, _world.OwnerUserId, key, Ct);
        }

        Assert.Equal(parts.Length, await _db.InternalActivities.AsNoTracking().CountAsync(a => a.CompletedAt == null, Ct));
        var before = await SnapshotAsync();

        foreach (var key in parts)
            await service.ReopenPartAsync(_world.OrgId, _world.OwnerUserId, key, Ct);

        // The one thing the reopen writes is done (the intents are open again with no reminder, every "To do" is closed); nothing else changed.
        var rows = await _db.OnboardingPartStates.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(parts.Length, rows.Count);
        Assert.All(rows, row => Assert.Equal((OnboardingPartStatus.Open, (DateTime?)null, (Guid?)null), (row.Status, row.RemindAt, row.ActivityId)));
        Assert.Equal(0, await _db.InternalActivities.AsNoTracking().CountAsync(a => a.CompletedAt == null, Ct));
        Assert.Equal(before, await SnapshotAsync());
    }

    /// <summary>A part nobody provides has no wizard to go back to: 422 and nothing is written (no intent, no "To do").</summary>
    [Fact]
    public async Task Reopen_APartNobodyProvides_Is422_AndWritesNothing()
    {
        var service = Build(_db).Integration;

        foreach (var key in new[] { OnboardingPartKey.MessagesAi, OnboardingPartKey.FiscalDocuments, OnboardingPartKey.Partners })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.ReopenPartAsync(_world.OrgId, _world.OwnerUserId, key, Ct));
            Assert.Equal(OnboardingIntegrationCodes.PartNotReopenable, refused.Code);
        }

        Assert.Empty(await _db.OnboardingPartStates.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InternalActivities.AsNoTracking().ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReopenPartAsync(Guid.Empty, _world.OwnerUserId, OnboardingPartKey.Compliance, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReopenPartAsync(_world.OrgId, " ", OnboardingPartKey.Compliance, Ct));
    }

    /// <summary>The services with the real evaluators of the connection, the channels, the listings and the prices, and fixed ones for the rest.</summary>
    private (OnboardingIntegrationService Integration, OnboardingPlanService Plan) Build(AppDbContext db)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        var reader = new FlowPrerequisiteReader(db, new NoMappingReader(), environment.Object, NullLogger<FlowPrerequisiteReader>.Instance);
        IOnboardingPartEvaluator[] evaluators =
        [
            new OctorateConnectionPartEvaluator(db, reader),
            new ConstantEvaluator(OnboardingPartKey.Properties, new PartFacts(true, 0, 2, OnboardingPartReasons.MappingUnconfirmed)),
            new ChannelsPartEvaluator(db),
            new ListingsPartEvaluator(db),
            new PricesRulesPartEvaluator(db),
            new ConstantEvaluator(OnboardingPartKey.Compliance, new PartFacts(true, 0, 2, OnboardingPartReasons.SenderMissing)),
            new ConstantEvaluator(OnboardingPartKey.Identification, new PartFacts(true, 2, 2, null)),
            new NotProvidedPartEvaluator(OnboardingPartKey.MessagesAi),
            new NotProvidedPartEvaluator(OnboardingPartKey.FiscalDocuments),
            new NotProvidedPartEvaluator(OnboardingPartKey.Partners),
            new ConstantEvaluator(OnboardingPartKey.Activation, new PartFacts(true, 0, 2, OnboardingPartReasons.FlowsIncomplete)),
        ];
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, _clock);
        var plan = new OnboardingPlanService(db, evaluators, activities, _clock, NullLogger<OnboardingPlanService>.Instance);
        return (new OnboardingIntegrationService(db, plan, environment.Object, NullLogger<OnboardingIntegrationService>.Instance), plan);
    }

    private async Task<string> StateOfAsync(OnboardingPlanService plan, OnboardingPartKey key) =>
        (await plan.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct)).Parts.Single(p => p.Key == key).State;

    /// <summary>Every row a reopen must not touch, as text, in a stable order.</summary>
    private async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new object[]
    {
        await _db.PropertyComplianceSenders.AsNoTracking().OrderBy(s => s.PropertyId).ToListAsync(),
        await _db.ListingChannelStates.AsNoTracking().OrderBy(s => s.PropertyId).ThenBy(s => s.ChannelCode).ToListAsync(),
        await _db.OctorateConnections.AsNoTracking().OrderBy(c => c.OrgId).ThenBy(c => c.Environment).ToListAsync(),
        await _db.Properties.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.OrgId, p.Name, p.IsActive, p.RentalMode, p.Country, p.NightlyRate }).ToListAsync(),
    });

    private PropertyComplianceSender Sender(Property property, ComplianceSenderType sender) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        AlloggiatiSender = sender,
        IstatSender = sender,
        LastChangedAt = HostScopeScenario.Now.UtcDateTime,
        LastChangedByUserId = _world.OwnerUserId,
    };

    private static ListingChannelState Channel(Property property, string channelCode, ChannelPublishStatus status) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        ChannelCode = channelCode,
        Status = status,
    };

    /// <summary>The statuses that have channel states, with how many, in the order of the answer.</summary>
    private static (ChannelPublishStatus Status, int Count)[] Counted(OnboardingIntegrationOverview overview) =>
        [.. overview.Channels.Where(count => count.Count > 0).Select(count => (count.Status, count.Count))];

    /// <summary>How much of a part is done over how much there is to do, as the overview shows it for the reach of its caller.</summary>
    private static (int Done, int Total) ProgressOf(OnboardingIntegrationOverview overview, OnboardingPartKey key)
    {
        var part = overview.Parts.Single(p => p.Plan.Key == key).Plan;
        return (part.Done, part.Total);
    }

    private static string[] Names(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    /// <summary>The names of every member of the types, and of the types of their members, that belong to CasaZen (not the framework's).</summary>
    private static IEnumerable<string> MemberNamesOf(params Type[] roots)
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>(roots);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!seen.Add(type))
                continue;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return property.Name;
                foreach (var nested in new[] { property.PropertyType }.Concat(property.PropertyType.GenericTypeArguments))
                {
                    var underlying = Nullable.GetUnderlyingType(nested) ?? nested;
                    if (underlying.Namespace?.StartsWith("Casazen.", StringComparison.Ordinal) == true && !underlying.IsEnum)
                        pending.Enqueue(underlying);
                }
            }
        }
    }

    private static ClaimsPrincipal Principal(params string[] jwtRoles) =>
        OrgBillingAdminAuthorizationHandlerTests.Context(jwtRoles.Select(role => new Claim(ClaimTypes.Role, role))).User;

    /// <summary>A mapping nobody confirmed anything in: the evaluators of these tests never ask it.</summary>
    private sealed class NoMappingReader : IPropertyMappingReader
    {
        public bool IsProvided => true;

        public Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
            Guid orgId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken ct)
        {
            IReadOnlyDictionary<Guid, string> none = new Dictionary<Guid, string>();
            return Task.FromResult(none);
        }
    }

    /// <summary>An evaluator that always answers the same facts.</summary>
    private sealed class ConstantEvaluator(OnboardingPartKey part, PartFacts facts) : IOnboardingPartEvaluator
    {
        public OnboardingPartKey Part { get; } = part;

        public int Order => OnboardingPartEvaluatorOrders.Evaluator;

        public Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct) => Task.FromResult(facts);
    }
}
