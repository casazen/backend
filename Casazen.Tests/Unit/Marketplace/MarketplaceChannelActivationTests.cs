using System.Reflection;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F02/US01/T02, NF-56, MKT-03, D6: the steps behind the activation wizard of a channel. Proofs: the portal credentials are used for one call and
/// never stored or logged (privacy), a collaborator cannot reach a property or an activation outside its scope (permission), an org never reaches
/// another org's activation (data). The rules of the steps (one connection however many starts, Active only when the channel manager shows the
/// connection working, the pulls only after the mapping, the access on the panel read by the verifier) are checked with a stub of the client of the
/// channel manager: what is proven is what CasaZen asks, stores and answers, not Octorate.
/// </summary>
public class MarketplaceChannelActivationTests
{
    private const string Username = "portal-user-31b7c2";
    private const string Password = "portal-pass-9d4e6a";
    private const string HotelId = "H-4471";
    private const string Collaborator = "auth0|collaborator";

    // ─── Proofs ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarketplaceChannelActivation_PortalCredentials_NeverStoredOrLogged_Test()
    {
        var w = await World.CreateAsync();
        var start = new MarketplaceActivationStart(MarketplaceActivationKind.Channel, "142", w.PropertyId, HotelId, Username, Password);

        MarketplaceActivationState state;
        await using (var db = w.NewDb())
            state = await w.Service(db).StartAsync(w.OrgA, w.OrgWide, start);

        // The credentials went to the channel manager once, as the access data the portal asked for.
        var sent = Assert.Single(w.Created);
        Assert.Equal((HotelId, Username, Password), (sent.HotelId, sent.Username, sent.Password));

        // Not one column of the activation holds them, and no entity the service touched.
        await using (var db = w.NewDb())
        {
            var row = Assert.Single(await db.MarketplaceActivations.AsNoTracking().ToListAsync());
            Assert.Equal("7001", row.OctorateConnectionId);
            AssertNoSecret(Describe(row));
        }

        // Nor the answer, nor the request when it is printed.
        AssertNoSecret(JsonSerializer.Serialize(MarketplaceActivationStateDto.From(state)));
        AssertNoSecret(start.ToString());
        AssertNoSecret(sent.ToString());

        // A start whose answer is lost: the channel manager does not answer, the exception carries the password in its inner text. The activation is kept
        // without its id and without a trace of the credentials; the retry takes the connection that was made and creates no second one.
        var lost = await World.CreateAsync();
        lost.CreateFailure = new OctorateServerException(null, new HttpRequestException($"reset by peer, password={Password}"));
        await using (var db = lost.NewDb())
            await Assert.ThrowsAsync<OctorateServerException>(() => lost.Service(db).StartAsync(lost.OrgA, lost.OrgWide, start with { PropertyId = lost.PropertyId }));

        await using (var db = lost.NewDb())
        {
            var kept = Assert.Single(await db.MarketplaceActivations.AsNoTracking().ToListAsync());
            Assert.Null(kept.OctorateConnectionId);
            Assert.Equal("octorate_unavailable", kept.LastErrorCode);
            Assert.Equal(MarketplaceActivationStatus.Pending, kept.Status);
            AssertNoSecret(Describe(kept));
        }

        lost.CreateFailure = null;
        lost.Connections.Add(new OctorateConnectionInfo("7009", "142", "WAITING", null, null));
        await using (var db = lost.NewDb())
        {
            var retried = await lost.Service(db).StartAsync(lost.OrgA, lost.OrgWide, start with { PropertyId = lost.PropertyId });
            Assert.True(retried.ConnectionFound);
        }

        await using (var db = lost.NewDb())
        {
            var adopted = Assert.Single(await db.MarketplaceActivations.AsNoTracking().ToListAsync());
            Assert.Equal("7009", adopted.OctorateConnectionId);
            Assert.Null(adopted.LastErrorCode);
        }

        // The create was tried once, by the first start: the retry made no second connection.
        Assert.Single(lost.Created);

        // Not one line of the logs of either world.
        AssertNoSecret(w.Logs.AllOutput);
        AssertNoSecret(lost.Logs.AllOutput);
        Assert.Contains(w.Logs.Entries, e => e.Message.Contains("started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarketplaceChannelActivation_PropertyOutsideScope_Rejected_Test()
    {
        var w = await World.CreateAsync();

        // The owner starts the channel on the second property; the collaborator is granted the first one only.
        MarketplaceActivationState others;
        await using (var db = w.NewDb())
        {
            others = await w.Service(db).StartAsync(
                w.OrgA,
                w.OrgWide,
                new MarketplaceActivationStart(MarketplaceActivationKind.Channel, "142", w.OtherPropertyId, HotelId, Username, Password));
        }

        await w.GrantAsync(Collaborator, w.PropertyId);
        var scope = new HostScope(w.OrgA, GrantedToUserId: Collaborator);
        w.Client.Invocations.Clear();
        var before = await w.RowsAsync();

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // The plan and the start of a property outside the scope: the 404 of a property that does not exist.
            var plan = await Assert.ThrowsAsync<NotFoundException>(
                () => service.GetPlanAsync(w.OrgA, scope, MarketplaceActivationKind.Channel, "142", w.OtherPropertyId));
            Assert.Equal("marketplace_property_not_found", plan.Code);
            var started = await Assert.ThrowsAsync<NotFoundException>(
                () => service.StartAsync(
                    w.OrgA,
                    scope,
                    new MarketplaceActivationStart(MarketplaceActivationKind.Channel, "142", w.OtherPropertyId, HotelId, Username, Password)));
            Assert.Equal("marketplace_property_not_found", started.Code);

            // Every step on the activation of that property: the 404 of an activation that does not exist.
            var id = others.ActivationId;
            var steps = new Func<Task>[]
            {
                () => service.GetStateAsync(w.OrgA, scope, id),
                () => service.ImportAsync(w.OrgA, scope, id),
                () => service.GetMappingAsync(w.OrgA, scope, id),
                () => service.SaveMappingAsync(w.OrgA, scope, id, [new MarketplaceRoomChoice("3301", "956528")]),
                () => service.ActivateAsync(w.OrgA, scope, id),
            };
            foreach (var step in steps)
            {
                var refused = await Assert.ThrowsAsync<NotFoundException>(step);
                Assert.Equal("marketplace_activation_not_found", refused.Code);
            }
        }

        // Nothing left for the channel manager and nothing changed in CasaZen.
        Assert.Empty(w.Client.Invocations);
        Assert.Equal(before, await w.RowsAsync());

        // The same collaborator reaches the property it was given.
        await using (var db = w.NewDb())
        {
            var plan = await w.Service(db).GetPlanAsync(w.OrgA, scope, MarketplaceActivationKind.Channel, "142", w.PropertyId);
            Assert.True(plan.PropertyLinked);
        }
    }

    [Fact]
    public async Task MarketplaceChannelActivation_AnotherOrgsActivation_IsNeverReached()
    {
        var w = await World.CreateAsync();
        var state = await w.StartAsync();
        var orgB = Guid.NewGuid();
        w.Client.Invocations.Clear();

        await using var db = w.NewDb();
        var service = w.Service(db);
        var refused = await Assert.ThrowsAsync<NotFoundException>(() => service.GetStateAsync(orgB, new HostScope(orgB), state.ActivationId));
        Assert.Equal("marketplace_activation_not_found", refused.Code);

        // The scope of another org is a mistake of the caller, refused before anything is read.
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetStateAsync(orgB, new HostScope(w.OrgA), state.ActivationId));
        Assert.Empty(w.Client.Invocations);
    }

    // ─── The plan and the start ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPlanAsync_SaysWhatThePortalAsksFor_AndWhetherThePropertyIsLinked()
    {
        var w = await World.CreateAsync(portal => portal.RequiresTokenAuthentication = true);
        await using var db = w.NewDb();
        var plan = await w.Service(db).GetPlanAsync(w.OrgA, w.OrgWide, MarketplaceActivationKind.Channel, "142", w.PropertyId);

        Assert.Equal(["hotelId", "credentials", "panelAccess"], plan.Requirements);
        Assert.True(plan.Creatable);
        Assert.True(plan.PropertyLinked);
        Assert.Null(plan.ActivationId);

        // A property with no confirmed listing is answered as such, not refused; nothing was asked of the channel manager.
        var unlinked = await World.CreateAsync(linked: false);
        await using var db2 = unlinked.NewDb();
        var notLinked = await unlinked.Service(db2).GetPlanAsync(unlinked.OrgA, unlinked.OrgWide, MarketplaceActivationKind.Channel, "142", unlinked.PropertyId);
        Assert.False(notLinked.PropertyLinked);
        Assert.Empty(unlinked.Client.Invocations);
    }

    [Fact]
    public async Task StartAsync_Twice_CreatesOneConnectionAndOneActivation()
    {
        var w = await World.CreateAsync();
        var first = await w.StartAsync();

        // The second start carries no credentials (the wizard keeps none): it still gets the activation that exists, and nothing is created.
        var second = await w.StartAsync(hotelId: null, username: null, password: null);

        Assert.Equal(first.ActivationId, second.ActivationId);
        Assert.Equal(MarketplaceActivationStatus.Pending, second.Status);
        Assert.Single(w.Created);
        Assert.Single(await w.RowsAsync());

        // The plan of the same property and portal now points at the activation, to resume it.
        await using var db = w.NewDb();
        var plan = await w.Service(db).GetPlanAsync(w.OrgA, w.OrgWide, MarketplaceActivationKind.Channel, "142", w.PropertyId);
        Assert.Equal((first.ActivationId, MarketplaceActivationStatus.Pending), (plan.ActivationId, plan.ActivationStatus));
    }

    [Fact]
    public async Task StartAsync_AConnectionTheCasaZenDidNotMake_IsA409_AndTheCreateRefusedByTheChannelManagerLeavesNothing()
    {
        var w = await World.CreateAsync();
        w.Connections.Add(new OctorateConnectionInfo("7500", "142", "ENABLED", null, null));
        var exists = await Assert.ThrowsAsync<DomainConflictException>(() => w.StartAsync());
        Assert.Equal("marketplace_connection_exists", exists.Code);
        Assert.Empty(w.Created);
        Assert.Empty(await w.RowsAsync());

        var conflicting = await World.CreateAsync();
        conflicting.CreateFailure = new OctorateConflictException();
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => conflicting.StartAsync());
        Assert.Equal("marketplace_connection_exists", conflict.Code);
        Assert.Empty(await conflicting.RowsAsync());

        // A refusal of the access data, or the failure of the channel, leaves no activation either.
        var refused = await World.CreateAsync();
        refused.CreateFailure = new OctoratePartnerFailureException();
        await Assert.ThrowsAsync<OctoratePartnerFailureException>(() => refused.StartAsync());
        Assert.Empty(await refused.RowsAsync());
    }

    [Fact]
    public async Task StartAsync_ACreateTheCallerAbandoned_KeepsTheRow_AndTheNextStartTakesTheConnectionWithoutA409()
    {
        var w = await World.CreateAsync();
        using var abandoned = new CancellationTokenSource();

        // The caller goes away while the create is out: the channel manager may have made the connection.
        w.OnCreate = abandoned.Cancel;
        w.CreateFailure = new OperationCanceledException(abandoned.Token);
        await using (var db = w.NewDb())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Service(db).StartAsync(w.OrgA, w.OrgWide, w.StartRequest(), abandoned.Token));

        var kept = Assert.Single(await w.ActivationsAsync());
        Assert.Equal((MarketplaceActivationStatus.Pending, "octorate_unavailable"), (kept.Status, kept.LastErrorCode));
        Assert.Null(kept.OctorateConnectionId);
        AssertNoSecret(Describe(kept));

        // The connection is there: the next start takes it, on the same row, and neither makes a second one nor refuses it as one CasaZen did not make.
        w.OnCreate = null;
        w.CreateFailure = null;
        w.Connections.Add(new OctorateConnectionInfo("7009", "142", "WAITING", null, null));
        var restarted = await w.StartAsync();
        Assert.Equal((kept.Id, MarketplaceActivationStatus.Pending, true), (restarted.ActivationId, restarted.Status, restarted.ConnectionFound));
        Assert.Equal("7009", Assert.Single(await w.ActivationsAsync()).OctorateConnectionId);
        Assert.Single(w.Created);
    }

    [Fact]
    public async Task StartAsync_RefusesWithTheStableCodes_BeforeAnythingLeaves()
    {
        // The connector is off: nobody is connected.
        var off = await World.CreateAsync(flag: false);
        Assert.Equal("marketplace_octorate_not_connected", (await Assert.ThrowsAsync<DomainConflictException>(() => off.StartAsync())).Code);

        // The org has no connected account in this environment.
        var disconnected = await World.CreateAsync(connected: false);
        Assert.Equal("marketplace_octorate_not_connected", (await Assert.ThrowsAsync<DomainConflictException>(() => disconnected.StartAsync())).Code);

        // The property has no confirmed listing.
        var unlinked = await World.CreateAsync(linked: false);
        Assert.Equal("marketplace_property_not_linked", (await Assert.ThrowsAsync<DomainRuleException>(() => unlinked.StartAsync())).Code);

        // A portal the catalog does not offer, or offers as the other kind.
        var normal = await World.CreateAsync();
        Assert.Equal("marketplace_portal_unknown", (await Assert.ThrowsAsync<NotFoundException>(() => normal.StartAsync(code: "999"))).Code);
        Assert.Equal("marketplace_portal_unknown", (await Assert.ThrowsAsync<NotFoundException>(() => normal.StartAsync(kind: MarketplaceActivationKind.Tool))).Code);
        Assert.Equal("marketplace_portal_unknown", (await Assert.ThrowsAsync<NotFoundException>(() => normal.StartAsync(kind: MarketplaceActivationKind.Partner))).Code);

        // Octorate does not let the portal be created through its API, or its code is not the number it calls channelId.
        var notCreatable = await World.CreateAsync(portal => portal.Creatable = false);
        Assert.Equal("marketplace_portal_not_creatable", (await Assert.ThrowsAsync<DomainRuleException>(() => notCreatable.StartAsync())).Code);

        // What the portal asks for and the request does not give.
        var asking = await World.CreateAsync();
        foreach (var (hotel, user, pass) in new (string?, string?, string?)[]
                 {
                     (null, Username, Password),
                     ("  ", Username, Password),
                     (HotelId, null, Password),
                     (HotelId, Username, null),
                     (HotelId, " ", Password),
                 })
        {
            Assert.Equal(
                "marketplace_credentials_required",
                (await Assert.ThrowsAsync<DomainRuleException>(() => asking.StartAsync(hotelId: hotel, username: user, password: pass))).Code);
        }

        foreach (var world in new[] { off, disconnected, unlinked, normal, notCreatable, asking })
        {
            Assert.Empty(world.Created);
            Assert.Empty(await world.RowsAsync());
        }

        // A portal that asks for nothing is sent nothing: what the request carries beyond it is dropped.
        var bare = await World.CreateAsync(portal => (portal.RequiresHotelId, portal.RequiresPassword) = (false, false));
        await bare.StartAsync();
        var created = Assert.Single(bare.Created);
        Assert.Null(created.HotelId);
        Assert.Null(created.Username);
        Assert.Null(created.Password);
    }

    // ─── Import, mapping, activation ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_RunsTheThreeImportsInOrder_AndReadsTheStateAgain()
    {
        var w = await World.CreateAsync();
        var started = await w.StartAsync();
        w.Calls.Clear();

        MarketplaceActivationState state;
        await using (var db = w.NewDb())
            state = await w.Service(db).ImportAsync(w.OrgA, w.OrgWide, started.ActivationId);

        Assert.Equal(["ImportRooms", "ImportReservations", "ImportCalendar"], w.Calls);
        Assert.Equal((MarketplaceActivationStatus.Pending, true, "WAITING"), (state.Status, state.ConnectionFound, state.ConnectionStatus));
        Assert.Equal(w.Now, state.LastSyncedAt);
    }

    [Fact]
    public async Task ImportAndActivate_AChannelThatNeedsThePanel_WaitForTheAccess_AndTheVerifierReadsIt()
    {
        var w = await World.CreateAsync(portal => (portal.RequiresTokenAuthentication, portal.RequiresHotelId, portal.RequiresPassword) = (true, false, false));
        var started = await w.StartAsync();
        w.Rooms.Add(new OctorateChannelRoom("3301", "Double Room", "956528"));
        w.Calls.Clear();

        // The person has not given the access on the panel yet: nothing is asked of the channel, and the step says it cannot tell.
        Assert.False(started.PanelAccessDone);
        await using (var db = w.NewDb())
        {
            var service = w.Service(db);
            Assert.Equal("marketplace_panel_access_pending", (await Assert.ThrowsAsync<DomainConflictException>(() => service.ImportAsync(w.OrgA, w.OrgWide, started.ActivationId))).Code);
            Assert.Equal("marketplace_panel_access_pending", (await Assert.ThrowsAsync<DomainConflictException>(() => service.ActivateAsync(w.OrgA, w.OrgWide, started.ActivationId))).Code);
            Assert.Empty(w.Calls);

            var verifier = new ChannelAccessPanelVerifier(db, service);
            Assert.Equal((PanelStepKind.ChannelAccess, 10), (verifier.Kind, verifier.Order));
            Assert.Equal(PanelVerificationOutcome.Unverifiable, await verifier.VerifyAsync(w.Step(w.PropertyId, "142"), CancellationToken.None));
        }

        // The person came back from the panel: the channel manager says the access was given.
        w.Connections[0] = w.Connections[0] with { UserLogged = true };
        await using (var db = w.NewDb())
        {
            var service = w.Service(db);
            Assert.True((await service.GetStateAsync(w.OrgA, w.OrgWide, started.ActivationId)).PanelAccessDone);
            var verifier = new ChannelAccessPanelVerifier(db, service);
            Assert.Equal(PanelVerificationOutcome.Verified, await verifier.VerifyAsync(w.Step(w.PropertyId, "142"), CancellationToken.None));

            // Every other case is «cannot tell», never «not valid»: another property, another channel, another org, a step with no property.
            foreach (var step in new[]
                     {
                         w.Step(w.OtherPropertyId, "142"),
                         w.Step(w.PropertyId, "274"),
                         w.Step(w.PropertyId, null),
                         w.Step(null, "142"),
                         new PanelStepContext(Guid.NewGuid(), w.PropertyId, PanelStepKind.ChannelAccess, "142"),
                     })
            {
                Assert.Equal(PanelVerificationOutcome.Unverifiable, await verifier.VerifyAsync(step, CancellationToken.None));
            }

            var imported = await service.ImportAsync(w.OrgA, w.OrgWide, started.ActivationId);
            Assert.True(imported.PanelAccessDone);
        }

        Assert.Equal(["ImportRooms", "ImportReservations", "ImportCalendar"], w.Calls);

        // The verifier only reads: the activation is as the import left it.
        Assert.Equal(MarketplaceActivationStatus.Pending, Assert.Single(await w.ActivationsAsync()).Status);
    }

    [Fact]
    public async Task ChannelAccessPanelVerifier_AConnectorThatIsOff_IsUnverifiable_AndAFailureWorthARetryIsRaised()
    {
        var w = await World.CreateAsync();
        await w.StartAsync();

        w.FlagOn = false;
        await using (var db = w.NewDb())
        {
            var verifier = new ChannelAccessPanelVerifier(db, w.Service(db));
            Assert.Equal(PanelVerificationOutcome.Unverifiable, await verifier.VerifyAsync(w.Step(w.PropertyId, "142"), CancellationToken.None));
        }

        w.FlagOn = true;
        w.ListFailure = new OctorateRateLimitException(429);
        await using (var db = w.NewDb())
        {
            var verifier = new ChannelAccessPanelVerifier(db, w.Service(db));
            await Assert.ThrowsAsync<OctorateRateLimitException>(() => verifier.VerifyAsync(w.Step(w.PropertyId, "142"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task ChannelAccessPanelVerifier_AReadTheChannelManagerRefused_IsUnverifiable_AndOnlyAFailureWorthARetryIsRaised()
    {
        var w = await World.CreateAsync();
        await w.StartAsync();

        // A retry would not change the refusal of the token, of an accommodation it does not know, of a request it will not take, of the other
        // environment, of the channel itself: the person may confirm.
        var refusals = new OctorateException[]
        {
            OctorateAuthException.Refused(403),
            OctorateAuthException.NotConnected(),
            new OctorateNotFoundException(),
            new OctorateRequestRejectedException(400),
            new OctorateWrongEnvironmentException(403),
            new OctoratePartnerFailureException(),
        };
        foreach (var refusal in refusals)
        {
            w.ListFailure = refusal;
            await using var db = w.NewDb();
            var verifier = new ChannelAccessPanelVerifier(db, w.Service(db));
            Assert.Equal(PanelVerificationOutcome.Unverifiable, await verifier.VerifyAsync(w.Step(w.PropertyId, "142"), CancellationToken.None));
        }

        // What is worth a retry is raised: the step counts it as an attempt and decides nothing.
        var retries = new OctorateException[] { new OctorateServerException(503), new OctorateServerException(null), new OctorateRateLimitException(429) };
        foreach (var retry in retries)
        {
            w.ListFailure = retry;
            await using var db = w.NewDb();
            var verifier = new ChannelAccessPanelVerifier(db, w.Service(db));
            var raised = await Assert.ThrowsAnyAsync<OctorateException>(() => verifier.VerifyAsync(w.Step(w.PropertyId, "142"), CancellationToken.None));
            Assert.Same(retry, raised);
        }
    }

    [Fact]
    public async Task GetMappingAsync_ProposesOnlyAnExactNormalisedName()
    {
        var w = await World.CreateAsync();
        var started = await w.StartAsync();
        w.Products.Clear();
        w.Rooms.AddRange(
        [
            new OctorateChannelRoom("3301", "Camera  Doppia", null),
            new OctorateChannelRoom("3302", "SUITE vista-mare", null, "Standard"),
            new OctorateChannelRoom("3303", "Garden House", null, null, false),
            new OctorateChannelRoom("3304", "Loft", "956531"),
            new OctorateChannelRoom("3305", "Bilocale", null),
            new OctorateChannelRoom("3306", "Bilocale", null, "Non refundable"),
            new OctorateChannelRoom("3307", "Attico", null),
        ]);
        w.Products.AddRange(
        [
            new OctorateRoom { Id = "956528", Name = "camera doppia!" },
            new OctorateRoom { Id = "956529", Name = "Suite vista mare" },
            new OctorateRoom { Id = "956530", Name = "Garden House" },
            new OctorateRoom { Id = "956531", Name = "Loft" },
            new OctorateRoom { Id = "956532", Name = "Bilocale" },
            new OctorateRoom { Id = "956533", Name = "Attico" },
            new OctorateRoom { Id = "956534", Name = "attico" },
        ]);

        await using var db = w.NewDb();
        var mapping = await w.Service(db).GetMappingAsync(w.OrgA, w.OrgWide, started.ActivationId);

        var proposals = mapping.ChannelRooms.ToDictionary(room => room.ChannelRoomId, room => room.ProposedProductId);
        Assert.Equal("956528", proposals["3301"]);
        Assert.Equal("956529", proposals["3302"]);
        Assert.Null(proposals["3303"]); // the channel manager cannot manage it
        Assert.Null(proposals["3304"]); // mapped already
        Assert.Null(proposals["3305"]); // two rooms of the channel with the same name
        Assert.Null(proposals["3306"]);
        Assert.Null(proposals["3307"]); // two products with the same name
        Assert.Equal("956531", mapping.ChannelRooms.Single(room => room.ChannelRoomId == "3304").MappedProductId);
        Assert.Equal("956528", mapping.PropertyProductId);
        Assert.Equal(7, mapping.Products.Count);
    }

    [Fact]
    public async Task SaveMappingAsync_SavesThenCompletes_AndRefusesWhatIsNotTheChannelsOrTheListings()
    {
        var w = await World.CreateAsync();
        var started = await w.StartAsync();
        w.Products.Clear();
        w.Rooms.AddRange([new OctorateChannelRoom("3301", "Camera", null), new OctorateChannelRoom("3302", "Suite", null), new OctorateChannelRoom("3303", "Garden", null, null, false)]);
        w.Products.AddRange([new OctorateRoom { Id = "956528", Name = "Camera" }, new OctorateRoom { Id = "956529", Name = "Suite" }]);
        w.Calls.Clear();

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // An empty choice is never «all the rooms».
            Assert.Equal("marketplace_mapping_required", (await Assert.ThrowsAsync<DomainRuleException>(() => service.SaveMappingAsync(w.OrgA, w.OrgWide, started.ActivationId, []))).Code);

            // A room that is not the channel's (or cannot be managed), a product that is not the listing's, the same room twice, a blank id.
            var invalid = new[]
            {
                new[] { new MarketplaceRoomChoice("9999", "956528") },
                new[] { new MarketplaceRoomChoice("3303", "956528") },
                new[] { new MarketplaceRoomChoice("3301", "111111") },
                new[] { new MarketplaceRoomChoice("3301", "956528"), new MarketplaceRoomChoice("3301", "956529") },
                new[] { new MarketplaceRoomChoice("3301", " ") },
            };
            foreach (var choices in invalid)
            {
                Assert.Equal(
                    "marketplace_mapping_invalid",
                    (await Assert.ThrowsAsync<DomainRuleException>(() => service.SaveMappingAsync(w.OrgA, w.OrgWide, started.ActivationId, choices))).Code);
            }

            Assert.Empty(w.Calls);

            var state = await service.SaveMappingAsync(
                w.OrgA,
                w.OrgWide,
                started.ActivationId,
                [new MarketplaceRoomChoice("3301", "956528"), new MarketplaceRoomChoice("3302", "956529")]);
            Assert.Equal(MarketplaceActivationStatus.Pending, state.Status);
        }

        Assert.Equal(["SaveMapping", "CommitMappingAsCompleted"], w.Calls);
        Assert.Equal([new OctorateRoomMapping("956528", "3301"), new OctorateRoomMapping("956529", "3302")], w.Saved);
    }

    [Fact]
    public async Task ActivateAsync_IsActiveOnlyWhenTheChannelManagerShowsTheConnectionWorking_AndThePullsStartAfterTheMapping()
    {
        // The channel manager shows it working after «mark connected»: Active, the pulls are switched on, and a second click does nothing.
        var w = await World.CreateAsync();
        var started = await w.StartAsync();
        w.Rooms.Add(new OctorateChannelRoom("3301", "Camera", "956528"));
        w.Calls.Clear();

        MarketplaceActivationState active;
        await using (var db = w.NewDb())
        {
            var service = w.Service(db);
            active = await service.ActivateAsync(w.OrgA, w.OrgWide, started.ActivationId);
            Assert.Equal(MarketplaceActivationStatus.Active, active.Status);

            var calls = w.Calls.Count;
            Assert.Equal(MarketplaceActivationStatus.Active, (await service.ActivateAsync(w.OrgA, w.OrgWide, started.ActivationId)).Status);
            Assert.Equal(calls, w.Calls.Count);
        }

        Assert.Equal(["MarkConnected", "Patch"], w.Calls);
        var patch = Assert.Single(w.Patches);
        Assert.Equal((true, true), (patch.UpdateCalendar, patch.PullReservations));
        var row = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(w.Now, row.VerifiedAt);
        Assert.Equal(w.Now, row.LastSyncedAt);
        Assert.Null(row.LastErrorCode);
        Assert.False(row.IsDeclared);

        // The connection is not shown working: To fix with the code, no pulls.
        var waiting = await World.CreateAsync();
        var waitingStart = await waiting.StartAsync();
        waiting.Rooms.Add(new OctorateChannelRoom("3301", "Camera", "956528"));
        waiting.StatusAfterMarkConnected = "WAITING";
        await using (var db = waiting.NewDb())
        {
            var toFix = await waiting.Service(db).ActivateAsync(waiting.OrgA, waiting.OrgWide, waitingStart.ActivationId);
            Assert.Equal((MarketplaceActivationStatus.ToFix, "connection_not_enabled"), (toFix.Status, toFix.LastErrorCode));
        }

        Assert.Empty(waiting.Patches);

        // The connection is gone from the list.
        var gone = await World.CreateAsync();
        var goneStart = await gone.StartAsync();
        gone.Rooms.Add(new OctorateChannelRoom("3301", "Camera", "956528"));
        gone.StatusAfterMarkConnected = null;
        await using (var db = gone.NewDb())
        {
            var lost = await gone.Service(db).ActivateAsync(gone.OrgA, gone.OrgWide, goneStart.ActivationId);
            Assert.Equal((MarketplaceActivationStatus.ToFix, "connection_missing"), (lost.Status, lost.LastErrorCode));
            Assert.False(lost.ConnectionFound);
        }

        // Gone after «mark connected»: the activation is freed (no id), so that the next start makes a connection.
        Assert.Null(Assert.Single(await gone.ActivationsAsync()).OctorateConnectionId);

        // No room mapped yet: «mark connected» is never sent (D6), the activation stays as it is.
        var unmapped = await World.CreateAsync();
        var unmappedStart = await unmapped.StartAsync();
        unmapped.Rooms.Add(new OctorateChannelRoom("3301", "Camera", null));
        unmapped.Calls.Clear();
        await using (var db = unmapped.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => unmapped.Service(db).ActivateAsync(unmapped.OrgA, unmapped.OrgWide, unmappedStart.ActivationId));
            Assert.Equal("marketplace_mapping_required", refused.Code);
        }

        Assert.Empty(unmapped.Calls);
        Assert.Equal(MarketplaceActivationStatus.Pending, Assert.Single(await unmapped.ActivationsAsync()).Status);

        // The channel itself fails (550): To fix with the code, and the failure reaches the caller.
        var failing = await World.CreateAsync();
        var failingStart = await failing.StartAsync();
        failing.Rooms.Add(new OctorateChannelRoom("3301", "Camera", "956528"));
        failing.ActionFailures[OctorateConnectionAction.MarkConnected] = new OctoratePartnerFailureException();
        await using (var db = failing.NewDb())
            await Assert.ThrowsAsync<OctoratePartnerFailureException>(() => failing.Service(db).ActivateAsync(failing.OrgA, failing.OrgWide, failingStart.ActivationId));

        var failed = Assert.Single(await failing.ActivationsAsync());
        Assert.Equal((MarketplaceActivationStatus.ToFix, "octorate_partner_failed"), (failed.Status, failed.LastErrorCode));

        // A failure worth a retry changes nothing.
        var retry = await World.CreateAsync();
        var retryStart = await retry.StartAsync();
        retry.Rooms.Add(new OctorateChannelRoom("3301", "Camera", "956528"));
        retry.ActionFailures[OctorateConnectionAction.MarkConnected] = new OctorateServerException(503);
        await using (var db = retry.NewDb())
            await Assert.ThrowsAsync<OctorateServerException>(() => retry.Service(db).ActivateAsync(retry.OrgA, retry.OrgWide, retryStart.ActivationId));

        Assert.Equal(MarketplaceActivationStatus.Pending, Assert.Single(await retry.ActivationsAsync()).Status);
    }

    [Fact]
    public async Task StartAsync_AfterTheActivationWasDeactivated_StartsAgainOnTheSameRow_AndALostAnswerLeavesNoStaleConnection()
    {
        var w = await World.CreateAsync();
        var first = await w.StartAsync();

        // The host turned it off: the row is kept as Deactivated and the connection is gone from the channel manager.
        await using (var db = w.NewDb())
        {
            var row = await db.MarketplaceActivations.SingleAsync();
            row.Status = MarketplaceActivationStatus.Deactivated;
            await db.SaveChangesAsync();
        }

        w.Connections.Clear();

        // The channel manager does not answer the new create: the row keeps no id (the old one belonged to the connection that is gone).
        w.CreateFailure = new OctorateServerException(null);
        await Assert.ThrowsAsync<OctorateServerException>(() => w.StartAsync());
        var kept = Assert.Single(await w.ActivationsAsync());
        Assert.Equal((first.ActivationId, MarketplaceActivationStatus.Pending), (kept.Id, kept.Status));
        Assert.Null(kept.OctorateConnectionId);

        // The channel manager did make it: the next start takes it, on the same row, and makes no third connection.
        w.CreateFailure = null;
        w.Connections.Add(new OctorateConnectionInfo("7002", "142", "WAITING", null, null));
        var restarted = await w.StartAsync();
        Assert.Equal((first.ActivationId, MarketplaceActivationStatus.Pending), (restarted.ActivationId, restarted.Status));
        Assert.Equal("7002", Assert.Single(await w.ActivationsAsync()).OctorateConnectionId);
        Assert.Equal(2, w.Created.Count);
    }

    // ─── A connection that is gone, a create the caller abandoned, a refused read, the D6 flags ───────────────

    [Fact]
    public async Task EveryStep_ThatFindsTheConnectionGone_FreesTheActivation_AndTheNextStartMakesAConnectionOnTheSameRow()
    {
        foreach (var step in new[] { "import", "mapping", "save", "activate" })
        {
            var w = await World.CreateAsync();
            var first = await w.StartAsync();
            w.Rooms.Add(new OctorateChannelRoom("3301", "Camera", "956528"));

            // The connection was removed on the channel manager: the step answers the 409 and the row stops pointing at it.
            w.Connections.Clear();
            w.Calls.Clear();
            await using (var db = w.NewDb())
            {
                var service = w.Service(db);
                Func<Task> run = step switch
                {
                    "import" => () => service.ImportAsync(w.OrgA, w.OrgWide, first.ActivationId),
                    "mapping" => () => service.GetMappingAsync(w.OrgA, w.OrgWide, first.ActivationId),
                    "save" => () => service.SaveMappingAsync(w.OrgA, w.OrgWide, first.ActivationId, [new MarketplaceRoomChoice("3301", "956528")]),
                    _ => () => service.ActivateAsync(w.OrgA, w.OrgWide, first.ActivationId),
                };
                Assert.Equal("marketplace_connection_missing", (await Assert.ThrowsAsync<DomainConflictException>(run)).Code);
            }

            var freed = Assert.Single(await w.ActivationsAsync());
            Assert.Equal((first.ActivationId, MarketplaceActivationStatus.ToFix, "connection_missing"), (freed.Id, freed.Status, freed.LastErrorCode));
            Assert.Null(freed.OctorateConnectionId);
            Assert.Empty(w.Calls);

            // The start goes through the list and makes a connection on the same row.
            var restarted = await w.StartAsync();
            Assert.Equal((first.ActivationId, MarketplaceActivationStatus.Pending, true), (restarted.ActivationId, restarted.Status, restarted.ConnectionFound));
            Assert.Equal(2, w.Created.Count);
            var row = Assert.Single(await w.ActivationsAsync());
            Assert.NotNull(row.OctorateConnectionId);
            Assert.Null(row.LastErrorCode);
        }

        // An activation the host turned off stays turned off when its connection is found gone: it only forgets the id.
        var off = await World.CreateAsync();
        var offStart = await off.StartAsync();
        await using (var db = off.NewDb())
        {
            var row = await db.MarketplaceActivations.SingleAsync();
            row.Status = MarketplaceActivationStatus.Deactivated;
            await db.SaveChangesAsync();
        }

        off.Connections.Clear();
        await using (var db = off.NewDb())
            await Assert.ThrowsAsync<DomainConflictException>(() => off.Service(db).ImportAsync(off.OrgA, off.OrgWide, offStart.ActivationId));

        var turnedOff = Assert.Single(await off.ActivationsAsync());
        Assert.Equal(MarketplaceActivationStatus.Deactivated, turnedOff.Status);
        Assert.Null(turnedOff.OctorateConnectionId);

        // The list of the channel manager may lag just after a create: a second start (a double click) gets the row it has and makes no second
        // connection, and a read of the state changes nothing.
        var lag = await World.CreateAsync();
        var created = await lag.StartAsync();
        lag.Connections.Clear();
        var again = await lag.StartAsync(hotelId: null, username: null, password: null);
        Assert.Equal((created.ActivationId, false), (again.ActivationId, again.ConnectionFound));
        await using (var db = lag.NewDb())
            Assert.False((await lag.Service(db).GetStateAsync(lag.OrgA, lag.OrgWide, created.ActivationId)).ConnectionFound);

        Assert.Single(lag.Created);
        Assert.Equal("7001", Assert.Single(await lag.ActivationsAsync()).OctorateConnectionId);
    }

    // ─── Registration and the proposer ────────────────────────────────────────────────────────────────────

    /// <summary>The exact list of what the activation registers: the service, and the verifier of the channel access with the order that beats the default one.</summary>
    [Fact]
    public void Registration_TheServiceAndTheVerifierOfTheChannelAccess()
    {
        var services = new ServiceCollection();
        services.AddCasazenMarketplace(new ConfigurationBuilder().Build());

        Assert.Equal(
            typeof(MarketplaceChannelActivationService),
            Assert.Single(services, d => d.ServiceType == typeof(IMarketplaceChannelActivationService)).ImplementationType);
        Assert.Equal(
            [typeof(ChannelAccessPanelVerifier)],
            services.Where(d => d.ServiceType == typeof(IPanelStepVerifier)).Select(d => d.ImplementationType));
        Assert.True(new ChannelAccessPanelVerifier(null!, null!).Order > new ChannelAccessDefaultPanelVerifier().Order);
    }

    [Theory]
    [InlineData("Camera  Doppia", "camera doppia")]
    [InlineData("  SUITE vista-mare!", "suite vista mare")]
    [InlineData("Città Vecchia 2", "citta vecchia 2")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ChannelRoomMappingProposer_Normalize_IsCaseAccentPunctuationAndSpaceInsensitive(string? name, string expected) =>
        Assert.Equal(expected, ChannelRoomMappingProposer.Normalize(name));

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertNoSecret(string text)
    {
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Username, text, StringComparison.Ordinal);
    }

    /// <summary>Every public member of an entity as text, so that a secret in any column of it is found.</summary>
    private static string Describe(object entity) =>
        string.Join(
            "; ",
            entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => $"{property.Name}={property.GetValue(entity)}"));

    /// <summary>An org with a connected account, two properties each with a confirmed listing, the portal «142» in the catalog, and a stub of the channel manager.</summary>
    private sealed class World
    {
        private const string Accommodation = "585492";
        private const string OtherAccommodation = "585493";

        public static readonly Guid FixedOrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a91");

        public Guid OrgA => FixedOrgA;

        public Guid PropertyId { get; private set; }

        public Guid OtherPropertyId { get; private set; }

        public HostScope OrgWide => new(OrgA);

        public DateTime Now => Clock.GetUtcNow().UtcDateTime;

        public bool FlagOn { get; set; } = true;

        public DbContextOptions<AppDbContext> Options { get; } =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"activation-{Guid.NewGuid()}").Options;

        public Mock<IOctorateApiClient> Client { get; } = new();

        public CapturingLogger<MarketplaceChannelActivationService> Logs { get; } = new();

        public FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero));

        /// <summary>The connections the channel manager lists; a create adds one.</summary>
        public List<OctorateConnectionInfo> Connections { get; } = [];

        public List<OctorateConnectionCreate> Created { get; } = [];

        public List<OctorateConnectionPatch> Patches { get; } = [];

        public List<OctorateRoomMapping> Saved { get; } = [];

        /// <summary>The actions and writes that reached the channel manager, in order.</summary>
        public List<string> Calls { get; } = [];

        public List<OctorateChannelRoom> Rooms { get; } = [];

        public List<OctorateRoom> Products { get; } = [new OctorateRoom { Id = "956528", Name = "Double Room" }];

        public Dictionary<OctorateConnectionAction, Exception> ActionFailures { get; } = [];

        public Exception? CreateFailure { get; set; }

        /// <summary>Runs when a create reaches the channel manager, before it answers (a caller that goes away while the create is out).</summary>
        public Action? OnCreate { get; set; }

        public Exception? ListFailure { get; set; }

        /// <summary>The status the connection has once «mark connected» ran; <c>null</c> removes it from the list.</summary>
        public string? StatusAfterMarkConnected { get; set; } = "ENABLED";

        public static async Task<World> CreateAsync(
            Action<MarketplacePortal>? portal = null,
            bool connected = true,
            bool linked = true,
            bool flag = true)
        {
            var world = new World { FlagOn = flag };
            await world.SeedAsync(portal, connected, linked);
            world.WireClient();
            return world;
        }

        public AppDbContext NewDb() => new(Options);

        public MarketplaceChannelActivationService Service(AppDbContext db)
        {
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(() => FlagOn);
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            return new MarketplaceChannelActivationService(
                db, Client.Object, new OctorateOrgScope(NullTenantContext.Instance), flags.Object, host.Object, Clock, Logs);
        }

        /// <summary>The start of the first property for the portal «142», with the credentials, as the wizard sends it.</summary>
        public MarketplaceActivationStart StartRequest() =>
            new(MarketplaceActivationKind.Channel, "142", PropertyId, HotelId, Username, Password);

        /// <summary>Starts the activation of the first property for the portal «142» with credentials, as the wizard does.</summary>
        public async Task<MarketplaceActivationState> StartAsync(
            string code = "142",
            MarketplaceActivationKind kind = MarketplaceActivationKind.Channel,
            string? hotelId = HotelId,
            string? username = Username,
            string? password = Password)
        {
            await using var db = NewDb();
            return await Service(db).StartAsync(OrgA, OrgWide, new MarketplaceActivationStart(kind, code, PropertyId, hotelId, username, password));
        }

        public PanelStepContext Step(Guid? propertyId, string? channelCode) =>
            new(OrgA, propertyId, PanelStepKind.ChannelAccess, channelCode);

        public async Task GrantAsync(string userId, Guid propertyId)
        {
            await using var db = NewDb();
            db.Set<PropertyMemberAccess>().Add(new PropertyMemberAccess { OrgId = OrgA, UserId = userId, PropertyId = propertyId });
            await db.SaveChangesAsync();
        }

        public async Task<List<MarketplaceActivation>> ActivationsAsync()
        {
            await using var db = NewDb();
            return await db.MarketplaceActivations.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync();
        }

        /// <summary>The activations as text, to compare before and after.</summary>
        public async Task<List<string>> RowsAsync() => (await ActivationsAsync()).Select(Describe).ToList();

        private async Task SeedAsync(Action<MarketplacePortal>? portal, bool connected, bool linked)
        {
            var connectionKey = Guid.NewGuid();
            var first = NewProperty();
            var second = NewProperty();
            PropertyId = first.Id;
            OtherPropertyId = second.Id;

            var catalog = new MarketplacePortal
            {
                PortalCode = "142",
                Name = "Portal 142",
                Category = MarketplacePortalCategory.Ota,
                Creatable = true,
                Enabled = true,
                IsListed = true,
                RequiresHotelId = true,
                RequiresPassword = true,
            };
            portal?.Invoke(catalog);

            await using var db = NewDb();
            db.OctorateConnections.Add(new OctorateConnection
            {
                Id = connectionKey,
                OrgId = OrgA,
                Environment = OctorateEnvironment.Test,
                Status = connected ? OctorateConnectionStatus.Connected : OctorateConnectionStatus.Disconnected,
            });
            db.Properties.AddRange(first, second);
            db.MarketplacePortals.Add(catalog);
            if (linked)
            {
                db.OctorateListingMappings.AddRange(
                    Listing(connectionKey, first.Id, Accommodation, "956528"),
                    Listing(connectionKey, second.Id, OtherAccommodation, "956540"));
            }

            await db.SaveChangesAsync();
        }

        private OctorateListingMapping Listing(Guid connectionKey, Guid propertyId, string accommodation, string product) =>
            new()
            {
                OrgId = OrgA,
                ConnectionId = connectionKey,
                OctorateAccommodationId = accommodation,
                PropertyId = propertyId,
                Status = OctorateMappingStatus.Confirmed,
                OctorateProductId = product,
            };

        private Property NewProperty() =>
            new()
            {
                OrgId = OrgA,
                OwnerId = "auth0|owner",
                Name = "Casa " + Guid.NewGuid().ToString("N")[..6],
                Address = "Via Prova 1",
                City = "Como",
                MaxGuests = 4,
            };

        /// <summary>The channel manager: it lists what <see cref="Connections"/> holds, makes the connection a create asks for, and keeps what it is sent.</summary>
        private void WireClient()
        {
            Client
                .Setup(c => c.ListConnectionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => ListFailure is { } failure
                    ? Task.FromException<OctorateConnectionInfo[]>(failure)
                    : Task.FromResult(Connections.ToArray()));

            Client
                .Setup(c => c.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<OctorateConnectionCreate>(), It.IsAny<CancellationToken>()))
                .Returns((string _, OctorateConnectionCreate request, CancellationToken _) =>
                {
                    Created.Add(request);
                    OnCreate?.Invoke();
                    if (CreateFailure is { } failure)
                        return Task.FromException<OctorateConnectionInfo>(failure);

                    var info = new OctorateConnectionInfo("7001", request.PortalCode, "WAITING", null, null);
                    Connections.Add(info);
                    return Task.FromResult(info);
                });

            Client
                .Setup(c => c.RunConnectionActionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctorateConnectionAction>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string _, OctorateConnectionAction action, CancellationToken _) =>
                {
                    Calls.Add(action.ToString());
                    if (ActionFailures.TryGetValue(action, out var failure))
                        return Task.FromException<IReadOnlyList<OctorateRoomMapping>>(failure);

                    if (action == OctorateConnectionAction.MarkConnected)
                    {
                        for (var i = 0; i < Connections.Count; i++)
                        {
                            if (StatusAfterMarkConnected is null)
                            {
                                Connections.RemoveAt(i--);
                                continue;
                            }

                            Connections[i] = Connections[i] with { Status = StatusAfterMarkConnected };
                        }
                    }

                    return Task.FromResult<IReadOnlyList<OctorateRoomMapping>>([]);
                });

            Client
                .Setup(c => c.GetConnectionMappingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Rooms.ToArray()));

            Client
                .Setup(c => c.GetRoomsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Products.ToArray()));

            Client
                .Setup(c => c.SaveConnectionMappingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<OctorateRoomMapping>>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string _, IReadOnlyList<OctorateRoomMapping> mappings, CancellationToken _) =>
                {
                    Calls.Add("SaveMapping");
                    Saved.AddRange(mappings);
                    return Task.CompletedTask;
                });

            Client
                .Setup(c => c.UpdateConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctorateConnectionPatch>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string _, OctorateConnectionPatch patch, CancellationToken _) =>
                {
                    Calls.Add("Patch");
                    Patches.Add(patch);
                    return Task.CompletedTask;
                });
        }
    }
}
