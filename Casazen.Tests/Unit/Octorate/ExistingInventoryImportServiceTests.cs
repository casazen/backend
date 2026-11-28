using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the import of the listings that already exist on OTAs (E02/F01/US03/T01, D6): nothing of it ever creates or publishes a
/// listing (data rule); no secret of an OTA enters CasaZen, its answers or its logs (privacy rule); another organization's property is never
/// selected (permission rule); a repeated command or a restarted worker imports and marks each external item at most once (data rule). Next
/// to them, the rules around the same data: a listing left for later is never marked connected, the access is confirmed by hand only after the
/// check could not tell, and existing inventory is refused before a listing is created on Octorate. The Octorate client is a strict mock: a
/// call nobody set up fails the test.
/// </summary>
public class ExistingInventoryImportServiceTests
{
    private static readonly Guid OrgA = Guid.Parse("c1a0b2d3-4e5f-4a6b-8c7d-9e0f1a2b3c01");
    private static readonly Guid OrgB = Guid.Parse("d2b1c3e4-5f60-4b7c-9d8e-0f1a2b3c4d02");
    private const string UserA = "auth0|host-a";
    private const string UserB = "auth0|host-b";
    private const string PanelUrl = "https://panel.example.test/octobook";

    private static readonly string[] AllowedClientCalls =
    [
        nameof(IOctorateApiClient.GetOrgConnectionsAsync),
        nameof(IOctorateApiClient.ImportRoomsAsync),
        nameof(IOctorateApiClient.ImportReservationsAsync),
        nameof(IOctorateApiClient.ImportCalendarAsync),
        nameof(IOctorateApiClient.MarkConnectedAsync),
    ];

    private static readonly string[] SecretWords =
        ["password", "passwd", "secret", "token", "credential", "cookie", "username", "apikey", "authorization"];

    private static ExistingInventoryCaller CallerA => new(OrgA, UserA);

    [Fact]
    public async Task ExistingInventoryImport_NeverCreatesOrPublishesListing_Test()
    {
        // Every state the import goes through, every kind of failure and the retries after them: the Octorate client is asked for the
        // connections and for the four operations of the D6 sequence and for nothing else, the mapping is only read, and no property is made.
        var harnesses = new List<Harness>();

        var unverified = new Harness();
        harnesses.Add(unverified);
        await unverified.SeedAsync(verified: false);
        var view = await unverified.RunToRoomsAsync();
        Assert.Equal(OctorateInventoryImportStatus.NeedsAttention, view.Status);
        Assert.Equal(OctorateInventoryErrorCodes.D6OperationUnverified, view.Channels[0].ErrorCode);
        Assert.Equal(1, unverified.Calls(nameof(IOctorateApiClient.GetOrgConnectionsAsync)));
        Assert.Equal(0, unverified.Calls(nameof(IOctorateApiClient.ImportRoomsAsync)));

        // 2. The rate limit stops the channel at the first room; the retry goes through.
        var limited = new Harness();
        harnesses.Add(limited);
        await limited.SeedAsync(verified: true);
        limited.Failures[(OctorateD6Operation.ImportRooms, "501")] = new OctorateRateLimitException(null);
        view = await limited.RunToRoomsAsync();
        Assert.Equal(new OctorateRateLimitException(null).Code, view.Channels[0].ErrorCode);
        Assert.Equal(1, limited.Calls(nameof(IOctorateApiClient.ImportRoomsAsync)));
        limited.Failures.Clear();
        await using (var db = limited.NewDb())
            view = await limited.Service(db).RunAsync(CallerA, view.Id, CancellationToken.None);
        Assert.Equal(OctorateInventoryImportStatus.AwaitingMapping, view.Channels[0].Status);

        var failing = new Harness();
        harnesses.Add(failing);
        await failing.SeedAsync(verified: true);
        view = await failing.RunToRoomsAsync();
        failing.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, failing.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Confirmed, failing.Properties[1])]);
        failing.Failures[(OctorateD6Operation.ImportReservations, "501")] = new OctorateServerException(550);
        failing.Failures[(OctorateD6Operation.ImportReservations, "502")] = new OctorateServerException(504);
        await using (var db = failing.NewDb())
            view = await failing.Service(db).ContinueAsync(CallerA, view.Id, null, CancellationToken.None);
        Assert.Equal(OctorateInventoryImportStatus.NeedsAttention, view.Channels[0].Status);
        Assert.Contains(view.Channels[0].Items, i => i.ErrorCode == OctorateInventoryErrorCodes.D6ChannelFailed);
        Assert.Contains(view.Channels[0].Items, i => i.ErrorCode == OctorateInventoryErrorCodes.D6ResultUnknown);
        failing.Failures.Clear();
        await using (var db = failing.NewDb())
            view = await failing.Service(db).ContinueAsync(CallerA, view.Id, null, CancellationToken.None, ["1002"]);
        Assert.Equal(OctorateInventoryImportStatus.Completed, view.Status);

        var missing = new Harness();
        harnesses.Add(missing);
        await missing.SeedAsync(verified: true);
        missing.Connections.Clear();
        view = await missing.RunToRoomsAsync();
        Assert.Equal(OctorateInventoryErrorCodes.ChannelConnectionNotFound, view.Channels[0].ErrorCode);

        var complete = new Harness();
        harnesses.Add(complete);
        await complete.SeedAsync(verified: true);
        view = await complete.RunToRoomsAsync();
        complete.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, complete.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Confirmed, complete.Properties[1])]);
        await using (var db = complete.NewDb())
            view = await complete.Service(db).ContinueAsync(CallerA, view.Id, null, CancellationToken.None);
        Assert.Equal(OctorateInventoryImportStatus.Completed, view.Status);

        foreach (var harness in harnesses)
        {
            var clientCalls = harness.Client.Invocations.Select(i => i.Method.Name).Distinct().ToList();
            Assert.All(clientCalls, name => Assert.Contains(name, AllowedClientCalls));

            Assert.All(
                harness.Mappings.Invocations.Select(i => i.Method.Name).Distinct(),
                name => Assert.Contains(name, new[] { nameof(IChannelMappingService.ListAsync), nameof(IChannelMappingService.RefreshAsync) }));
            Assert.All(
                harness.Bookings.Invocations.Select(i => i.Method.Name).Distinct(),
                name => Assert.Equal(nameof(IBookingImportService.ImportAllAsync), name));

            await using var db = harness.NewDb();
            Assert.Equal(2, await db.Properties.CountAsync());
        }

        Assert.Equal(
            new[] { "ImportCalendar", "ImportReservations", "ImportRooms", "MarkConnected" },
            Enum.GetNames<OctorateD6Operation>().Order().ToArray());

        var dependencies = typeof(ExistingInventoryImportService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name).ToArray();
        Assert.DoesNotContain(
            dependencies,
            name => name.Contains("Creator", StringComparison.Ordinal) || name.Contains("Publish", StringComparison.Ordinal) || name.Contains("ContentPush", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingInventoryImport_SecretsNeverEnterCasaZen_Test()
    {
        Type[] types =
        [
            typeof(OctorateInventoryIntent), typeof(OctorateInventoryImportBatch), typeof(OctorateInventoryImportChannel), typeof(OctorateInventoryImportItem),
            typeof(CreateExistingInventoryBatchRequest), typeof(RunExistingInventoryRequest), typeof(ContinueExistingInventoryRequest), typeof(InventoryIntentsRequest),
            typeof(InventoryIntentAssignmentRequest), typeof(ConnectExistingInventoryChannelRequest), typeof(ConnectExistingInventoryAccommodationRequest),
            typeof(ExistingInventoryConnectTarget),
            typeof(ExistingInventoryBatchDto), typeof(ChannelAccessStartDto), typeof(InventoryIntentDto), typeof(ExistingInventoryBatchView),
            typeof(ExistingInventoryChannelView), typeof(ExistingInventoryItemView), typeof(ExistingInventoryCounts), typeof(ExistingInventoryCapabilities),
            typeof(ChannelAccessStartResult), typeof(OctorateConnectionSummary), typeof(OctorateConnectionActionResult),
        ];
        foreach (var type in types)
        {
            var members = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Select(p => p.Name)
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Select(f => f.Name));
            foreach (var member in members)
            {
                Assert.False(
                    SecretWords.Any(word => member.Contains(word, StringComparison.OrdinalIgnoreCase)),
                    $"{type.Name}.{member} looks like a secret of an OTA: the access is given in the Octorate panel and never enters CasaZen.");
            }
        }

        foreach (var type in types.Where(t => t != typeof(ChannelAccessStartDto) && t != typeof(ChannelAccessStartResult)))
        {
            Assert.DoesNotContain(
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name),
                name => name.Contains("Url", StringComparison.OrdinalIgnoreCase));
        }

        var h = new Harness();
        await h.SeedAsync(verified: true);
        h.Handoff.State = ChannelAccessState.NotStarted;
        var logger = new CapturingLogger<ExistingInventoryImportService>();
        ExistingInventoryBatchView batch;
        ChannelAccessStartResult start;
        await using (var db = h.NewDb())
        {
            var service = h.Service(db, logger: logger);
            batch = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties(h.Properties), CancellationToken.None);
            start = await service.StartChannelAccessAsync(CallerA, batch.Id, "142", CancellationToken.None);
        }

        Assert.False(start.AlreadyComplete);
        Assert.Equal(PanelUrl, start.PanelUrl);
        Assert.Equal(PanelHandoffMode.ManagedWindow, start.HandoffMode);
        Assert.Single(h.Handoff.Started);
        Assert.Equal(batch.Id, h.Handoff.Started[0].BatchId);
        var uri = new Uri(start.PanelUrl!);
        Assert.Equal(string.Empty, uri.Query);
        Assert.Equal(string.Empty, uri.Fragment);
        foreach (var value in new[] { OrgA.ToString(), OrgA.ToString("N"), UserA, h.Properties[0].ToString(), h.Properties[0].ToString("N"), batch.Id.ToString() })
            Assert.DoesNotContain(value, start.PanelUrl!, StringComparison.OrdinalIgnoreCase);

        await using (var db = h.NewDb())
        {
            var stored = await db.OctorateInventoryImportChannels.AsNoTracking().SingleAsync();
            Assert.Equal(OctorateInventoryImportStatus.AwaitingChannelAccess, stored.Status);
            Assert.Equal(h.Handoff.StepId, stored.PanelStepId);

            h.Handoff.State = ChannelAccessState.Verified;
            var service = h.Service(db, logger: logger);
            var after = await service.GetBatchAsync(CallerA, batch.Id, CancellationToken.None);
            after = await service.RunAsync(CallerA, batch.Id, CancellationToken.None);
            var json = JsonSerializer.Serialize(ExistingInventoryBatchDto.From(after), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.DoesNotContain("panel.example.test", json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotEmpty(logger.Lines);
        foreach (var line in logger.Lines)
        {
            Assert.DoesNotContain("panel.example.test", line, StringComparison.OrdinalIgnoreCase);
            Assert.False(SecretWords.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase)), $"A log line names a secret: {line}");
        }
    }

    [Fact]
    public async Task ExistingInventoryImport_OtherOrgNeverSelected_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var mine = h.Properties[0];
        var theirs = await h.AddPropertyAsync(OrgB);

        await using (var db = h.NewDb())
        {
            var service = h.Service(db);
            var alone = await Assert.ThrowsAsync<NotFoundException>(
                () => service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties([theirs]), CancellationToken.None));
            Assert.Equal("octorate_property_not_found", alone.Code);
            var among = await Assert.ThrowsAsync<NotFoundException>(
                () => service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties([mine, theirs]), CancellationToken.None));
            Assert.Equal("octorate_property_not_found", among.Code);
            var assigned = await Assert.ThrowsAsync<NotFoundException>(
                () => service.SetIntentsAsync(CallerA, [new InventoryIntentAssignment(theirs, OctorateInventoryScenario.OnOtas, "Lago")], CancellationToken.None));
            Assert.Equal("octorate_property_not_found", assigned.Code);
        }

        await using (var db = h.NewDb())
        {
            Assert.Empty(await db.OctorateInventoryImportBatches.ToListAsync());
            Assert.Empty(await db.OctorateInventoryIntents.ToListAsync());
        }

        await using (var db = h.NewDb())
        {
            db.OctorateInventoryIntents.Add(new OctorateInventoryIntent
            {
                OrgId = OrgB,
                PropertyId = theirs,
                Scenario = OctorateInventoryScenario.OnOtas,
                WizardBatch = "Ville",
                ActorUserId = UserB,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = h.NewDb())
        {
            var service = h.Service(db);
            var foreignGroup = await Assert.ThrowsAsync<NotFoundException>(
                () => service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForGroup("Ville"), CancellationToken.None));
            Assert.Equal("existing_inventory_group_not_found", foreignGroup.Code);

            await service.SetIntentsAsync(CallerA, [new InventoryIntentAssignment(mine, OctorateInventoryScenario.OnOtas, "Lago")], CancellationToken.None);
            var group = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForGroup("Lago"), CancellationToken.None);
            Assert.Equal(new[] { mine }, group.PropertyIds);
            Assert.DoesNotContain(theirs, group.PropertyIds);
        }

        await using (var db = h.NewDb())
        {
            Assert.Empty(await db.OctorateInventoryIntents.Where(i => i.OrgId == OrgA && i.PropertyId == theirs).ToListAsync());
            Assert.Single(await db.OctorateInventoryIntents.Where(i => i.OrgId == OrgB).ToListAsync());
        }

        await using (var db = h.NewDb())
        {
            var limited = h.Service(db, new FakeScope(OrgA, orgWide: false));
            var wholeOrg = await Assert.ThrowsAsync<DomainForbiddenException>(
                () => limited.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.AllInventory, CancellationToken.None));
            Assert.Equal("existing_inventory_needs_whole_org", wholeOrg.Code);
            var notGiven = await Assert.ThrowsAsync<NotFoundException>(
                () => limited.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties([mine]), CancellationToken.None));
            Assert.Equal("octorate_property_not_found", notGiven.Code);

            var given = h.Service(db, new FakeScope(OrgA, orgWide: false, mine));
            var batch = await given.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties([mine]), CancellationToken.None);
            Assert.Equal(new[] { mine }, batch.PropertyIds);

            ExistingInventoryBatchView whole;
            await using (var adminDb = h.NewDb())
                whole = await h.Service(adminDb).CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.AllInventory, CancellationToken.None);
            var hidden = await Assert.ThrowsAsync<NotFoundException>(() => given.GetBatchAsync(CallerA, whole.Id, CancellationToken.None));
            Assert.Equal("existing_inventory_batch_not_found", hidden.Code);

            var otherOrg = h.Service(db, new FakeScope(OrgB));
            var foreignBatch = await Assert.ThrowsAsync<NotFoundException>(
                () => otherOrg.GetBatchAsync(new ExistingInventoryCaller(OrgB, UserB), whole.Id, CancellationToken.None));
            Assert.Equal("existing_inventory_batch_not_found", foreignBatch.Code);
        }
    }

    [Fact]
    public async Task ExistingInventoryImport_EmptyPropertySelectionIsNeverTheWholeInventory_Test()
    {
        var import = new Mock<IExistingInventoryImportService>();
        var view = new ExistingInventoryBatchView(
            Guid.NewGuid(),
            OctorateInventoryScopeKind.AllInventory,
            null,
            [],
            OctorateInventoryImportStatus.Draft,
            null,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            []);
        import
            .Setup(i => i.CreateBatchAsync(It.IsAny<ExistingInventoryCaller>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<ExistingInventoryScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(view);
        var controller = new OctorateExistingInventoryController(import.Object, new FixedOrg(OrgA))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = HostAuthorizationTestHarness.User(UserA, "PropertyOwner") },
            },
        };

        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => controller.Create(new CreateExistingInventoryBatchRequest { Channels = ["142"], PropertyIds = [] }, CancellationToken.None));
        Assert.Equal("existing_inventory_scope_invalid", empty.Code);
        import.Verify(
            i => i.CreateBatchAsync(It.IsAny<ExistingInventoryCaller>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<ExistingInventoryScope>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await controller.Create(new CreateExistingInventoryBatchRequest { Channels = ["142"] }, CancellationToken.None);
        import.Verify(
            i => i.CreateBatchAsync(
                It.IsAny<ExistingInventoryCaller>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.Is<ExistingInventoryScope>(s => s.Kind == OctorateInventoryScopeKind.AllInventory),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var property = Guid.NewGuid();
        await controller.Create(new CreateExistingInventoryBatchRequest { Channels = ["142"], PropertyIds = [property] }, CancellationToken.None);
        import.Verify(
            i => i.CreateBatchAsync(
                It.IsAny<ExistingInventoryCaller>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.Is<ExistingInventoryScope>(s => s.Kind == OctorateInventoryScopeKind.Properties && s.PropertyIds.SequenceEqual(new[] { property })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExistingInventoryImport_ReachOfTheCallerLimitsWhatIsShownAndTouched_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var mine = h.Properties[0];
        var colleagues = h.Properties[1];
        var limited = new FakeScope(OrgA, orgWide: false, mine);

        ExistingInventoryBatchView batch;
        await using (var db = h.NewDb())
        {
            var service = h.Service(db, limited);
            batch = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties([mine]), CancellationToken.None);
            await service.StartChannelAccessAsync(CallerA, batch.Id, "142", CancellationToken.None);
            batch = await service.RunAsync(CallerA, batch.Id, CancellationToken.None);
        }

        var longBefore = DateTime.UtcNow.AddDays(-30);
        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, mine), Harness.Row("1002", OctorateMappingStatus.Confirmed, colleagues, longBefore)]);

        await using (var db = h.NewDb())
        {
            var seen = await h.Service(db, limited).GetBatchAsync(CallerA, batch.Id, CancellationToken.None);
            Assert.Equal(1, seen.Channels[0].Counts.Found);
            Assert.Equal("1001", Assert.Single(seen.Channels[0].Items).OctorateAccommodationId);

            var whole = await h.Service(db).GetBatchAsync(CallerA, batch.Id, CancellationToken.None);
            Assert.Equal(2, whole.Channels[0].Counts.Found);
            Assert.Equal(1, whole.Channels[0].Counts.OutOfScope);
        }

        await using (var db = h.NewDb())
            batch = await h.Service(db, limited).ContinueAsync(CallerA, batch.Id, ["1002"], CancellationToken.None);

        Assert.Equal(OctorateInventoryImportStatus.Completed, batch.Channels[0].Status);
        Assert.Equal(1, batch.Channels[0].Counts.Connected);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        foreach (var method in new[] { nameof(IOctorateApiClient.ImportReservationsAsync), nameof(IOctorateApiClient.ImportCalendarAsync), nameof(IOctorateApiClient.MarkConnectedAsync) })
            Assert.Equal(0, h.Calls(method, "502"));

        Assert.Equal(new[] { mine }, h.Bookings.Invocations.Select(i => (Guid)i.Arguments[0]).Distinct().ToArray());
        await using (var db = h.NewDb())
        {
            Assert.Empty(await db.OctorateInventoryIntents.Where(i => i.PropertyId == colleagues).ToListAsync());
            var other = await db.OctorateInventoryImportItems.AsNoTracking().SingleAsync(i => i.OctorateConnectionId == "502");
            Assert.Null(other.LeftForLaterAt);
            Assert.Null(other.OctorateReservationsImportedAt);
        }

        h.Rows[1] = Harness.Row("1002", OctorateMappingStatus.Confirmed, colleagues, DateTime.UtcNow.AddHours(1));
        await using (var db = h.NewDb())
        {
            batch = await h.Service(db, limited).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);
            Assert.Equal(1, batch.Channels[0].Counts.Found);
        }

        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "502"));
        await using (var db = h.NewDb())
            Assert.Empty(await db.OctorateInventoryIntents.Where(i => i.PropertyId == colleagues).ToListAsync());

        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(2, batch.Channels[0].Counts.Connected);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "502"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));
        await using (var db = h.NewDb())
        {
            var declared = await db.OctorateInventoryIntents.AsNoTracking().SingleAsync(i => i.PropertyId == colleagues);
            Assert.Equal(OctorateInventoryScenario.OnOtas, declared.Scenario);
        }
    }

    [Fact]
    public async Task ExistingInventoryImport_ResumeIsIdempotent_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var properties = h.Properties;

        ExistingInventoryBatchView first;
        await using (var db = h.NewDb())
        {
            var service = h.Service(db);
            first = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties(properties), CancellationToken.None);
            var start = await service.StartChannelAccessAsync(CallerA, first.Id, "142", CancellationToken.None);
            Assert.True(start.AlreadyComplete);
            first = await service.RunAsync(CallerA, first.Id, CancellationToken.None);
        }

        Assert.Equal(OctorateInventoryImportStatus.AwaitingMapping, first.Channels[0].Status);
        Assert.Equal(2, first.Channels[0].Counts.Found);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync)));

        await using (var db = h.NewDb())
            first = await h.Service(db).RunAsync(CallerA, first.Id, CancellationToken.None);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync)));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.GetOrgConnectionsAsync)));

        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, properties[0]), Harness.Row("1002", OctorateMappingStatus.Confirmed, properties[1])]);
        h.Failures[(OctorateD6Operation.ImportCalendar, "502")] = new OctorateServerException(503);
        await using (var db = h.NewDb())
            first = await h.Service(db).ContinueAsync(CallerA, first.Id, null, CancellationToken.None);
        Assert.Equal(OctorateInventoryImportStatus.NeedsAttention, first.Channels[0].Status);
        Assert.Equal(OctorateInventoryErrorCodes.D6ResultUnknown, first.Channels[0].ErrorCode);
        Assert.Equal(1, first.Channels[0].Counts.Connected);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync)));
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportCalendarAsync)));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync)));

        h.Failures.Clear();
        await using (var db = h.NewDb())
            first = await h.Service(db).ContinueAsync(CallerA, first.Id, null, CancellationToken.None);
        Assert.Equal(OctorateInventoryImportStatus.NeedsAttention, first.Status);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportCalendarAsync)));

        await using (var db = h.NewDb())
            first = await h.Service(db).ContinueAsync(CallerA, first.Id, null, CancellationToken.None, ["1002"]);
        Assert.Equal(OctorateInventoryImportStatus.Completed, first.Status);
        Assert.Equal(2, first.Channels[0].Counts.Connected);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync)));
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync)));
        Assert.Equal(3, h.Calls(nameof(IOctorateApiClient.ImportCalendarAsync)));
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync)));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));

        var before = h.Client.Invocations.Count;
        await using (var db = h.NewDb())
            first = await h.Service(db).ContinueAsync(CallerA, first.Id, null, CancellationToken.None);
        Assert.Equal(before, h.Client.Invocations.Count);
        Assert.Equal(OctorateInventoryImportStatus.Completed, first.Status);

        ExistingInventoryBatchView second;
        await using (var db = h.NewDb())
        {
            var service = h.Service(db);
            second = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties(properties), CancellationToken.None);
            await service.StartChannelAccessAsync(CallerA, second.Id, "142", CancellationToken.None);
            second = await service.RunAsync(CallerA, second.Id, CancellationToken.None);
            second = await service.ContinueAsync(CallerA, second.Id, null, CancellationToken.None);
        }

        Assert.Equal(OctorateInventoryImportStatus.Completed, second.Status);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync)));
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync)));
        Assert.Equal(3, h.Calls(nameof(IOctorateApiClient.ImportCalendarAsync)));
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync)));
    }

    [Fact]
    public async Task ExistingInventoryImport_ResultNobodyKnowsIsNeverAskedAgainByItself_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var batch = await h.RunToRoomsAsync();
        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, h.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Confirmed, h.Properties[1])]);
        h.Failures[(OctorateD6Operation.ImportReservations, "501")] = new OperationCanceledException();

        await using (var db = h.NewDb())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None));
        }

        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "501"));
        await using (var db = h.NewDb())
        {
            var marked = await db.OctorateInventoryImportItems.AsNoTracking().SingleAsync(i => i.OctorateConnectionId == "501");
            Assert.Equal(OctorateInventoryErrorCodes.D6ResultUnknown, marked.ErrorCode);
            Assert.Null(marked.OctorateReservationsImportedAt);
        }

        h.Failures.Clear();
        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "502"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));
        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(OctorateInventoryImportStatus.NeedsAttention, batch.Channels[0].Status);
        Assert.Equal(OctorateInventoryErrorCodes.D6ResultUnknown, batch.Channels[0].ErrorCode);
        Assert.Equal(OctorateInventoryErrorCodes.D6ResultUnknown, batch.Channels[0].Items.Single(i => i.OctorateAccommodationId == "1001").ErrorCode);

        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None, ["1002"]);

        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "501"));

        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None, ["1001"]);

        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "501"));
        Assert.Equal(OctorateInventoryImportStatus.Completed, batch.Status);
        Assert.Equal(2, batch.Channels[0].Counts.Connected);
    }

    [Fact]
    public async Task ExistingInventoryImport_StampOfAnAnsweredCallSurvivesTheRequestBeingClosed_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var batch = await h.RunToRoomsAsync();
        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, h.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Confirmed, h.Properties[1])]);
        using var closed = new CancellationTokenSource();
        h.AfterAnswer = (operation, connection) =>
        {
            if (operation == OctorateD6Operation.MarkConnected && connection == "501")
                closed.Cancel();
        };

        await using (var db = h.NewDb())
        {
            try
            {
                await h.Service(db).ContinueAsync(CallerA, batch.Id, null, closed.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await using (var db = h.NewDb())
        {
            var answered = await db.OctorateInventoryImportItems.AsNoTracking().SingleAsync(i => i.OctorateConnectionId == "501");
            Assert.NotNull(answered.ConnectedAt);
            Assert.Null(answered.ErrorCode);
        }

        h.AfterAnswer = null;
        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(OctorateInventoryImportStatus.Completed, batch.Status);
    }

    [Fact]
    public async Task ExistingInventoryImport_OneListingThatFailsNeverHoldsTheOthersBack_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        h.Connections.Add(new OctorateConnectionSummary("505", "142", "1005", "H505", "WAITING", null));
        h.Failures[(OctorateD6Operation.ImportRooms, "502")] = new OctorateServerException(504);

        var batch = await h.RunToRoomsAsync();

        var channel = batch.Channels[0];
        Assert.Equal(OctorateInventoryImportStatus.AwaitingMapping, channel.Status);
        Assert.Null(channel.ErrorCode);
        Assert.NotNull(channel.RoomsImportedAt);
        Assert.Equal(2, channel.Counts.Found);
        Assert.Equal(1, channel.Counts.RoomsImported);
        Assert.Equal(1, channel.Counts.Failed);
        Assert.Equal(OctorateInventoryErrorCodes.D6ResultUnknown, channel.Items.Single(i => i.OctorateAccommodationId == "1002").ErrorCode);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync), "502"));
        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync), "505"));
        Assert.Equal(1, h.Mappings.Invocations.Count(i => i.Method.Name == nameof(IChannelMappingService.RefreshAsync)));

        await using (var db = h.NewDb())
            batch = await h.Service(db).RunAsync(CallerA, batch.Id, CancellationToken.None);

        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync), "502"));

        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, h.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Proposed, h.Properties[1])]);
        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        channel = batch.Channels[0];
        Assert.Equal(OctorateInventoryImportStatus.NeedsAttention, channel.Status);
        Assert.Equal(OctorateInventoryErrorCodes.D6ResultUnknown, channel.ErrorCode);
        Assert.Equal(1, channel.Counts.Connected);
        Assert.Equal(0, channel.Counts.Pending);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "502"));

        h.Failures.Clear();
        await using (var db = h.NewDb())
            batch = await h.Service(db).RunAsync(CallerA, batch.Id, CancellationToken.None, ["1002"]);

        Assert.Equal(2, batch.Channels[0].Counts.RoomsImported);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync), "502"));
        Assert.Equal(2, h.Mappings.Invocations.Count(i => i.Method.Name == nameof(IChannelMappingService.RefreshAsync)));

        h.Rows[1] = Harness.Row("1002", OctorateMappingStatus.Confirmed, h.Properties[1]);
        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(OctorateInventoryImportStatus.Completed, batch.Status);
        Assert.Equal(2, batch.Channels[0].Counts.Connected);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));
    }

    [Fact]
    public async Task ExistingInventoryImport_RejectingTheListingThatFailedTakesItOutOfTheImport_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        h.Failures[(OctorateD6Operation.ImportRooms, "502")] = new OctorateServerException(550);
        var batch = await h.RunToRoomsAsync();
        Assert.Equal(OctorateInventoryImportStatus.AwaitingMapping, batch.Channels[0].Status);
        Assert.Equal(OctorateInventoryErrorCodes.D6ChannelFailed, batch.Channels[0].Items.Single(i => i.OctorateAccommodationId == "1002").ErrorCode);

        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, h.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Rejected)]);
        await using (var db = h.NewDb())
            batch = await h.Service(db).RunAsync(CallerA, batch.Id, CancellationToken.None);

        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync), "502"));

        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(OctorateInventoryImportStatus.Completed, batch.Status);
        Assert.Equal(1, batch.Channels[0].Counts.Connected);
        Assert.Equal(1, batch.Channels[0].Counts.Rejected);
    }

    [Fact]
    public async Task ExistingInventoryImport_RoomImportStoresEachConnectionAsANewRow_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var view = await h.RunToRoomsAsync();

        Assert.Equal(OctorateInventoryImportStatus.AwaitingMapping, view.Channels[0].Status);
        await using var db = h.NewDb();
        var items = await db.OctorateInventoryImportItems.AsNoTracking().OrderBy(i => i.OctorateConnectionId).ToListAsync();
        Assert.Equal(new[] { "501", "502" }, items.Select(i => i.OctorateConnectionId).ToArray());
        Assert.All(items, item => Assert.NotNull(item.RoomsImportedAt));
        Assert.Equal(2, items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task ExistingInventoryImport_LeftForLaterRowIsNeverMarkedConnected_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        ExistingInventoryBatchView batch;
        await using (var db = h.NewDb())
        {
            var service = h.Service(db);
            batch = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties(h.Properties), CancellationToken.None);
            await service.StartChannelAccessAsync(CallerA, batch.Id, "142", CancellationToken.None);
            batch = await service.RunAsync(CallerA, batch.Id, CancellationToken.None);
        }

        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, h.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Ambiguous)]);
        await using (var db = h.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(
                () => h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None));
            Assert.Equal("existing_inventory_mapping_pending", refused.Code);
        }

        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync)));

        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, ["1002"], CancellationToken.None);

        Assert.Equal(OctorateInventoryImportStatus.Completed, batch.Channels[0].Status);
        Assert.Equal(1, batch.Channels[0].Counts.LeftForLater);
        Assert.Equal(1, batch.Channels[0].Counts.Connected);
        var left = batch.Channels[0].Items.Single(i => i.OctorateAccommodationId == "1002");
        Assert.True(left.LeftForLater);
        Assert.False(left.Connected);
        Assert.False(left.ReservationsImported);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync)));
        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));

        h.Rows[1] = Harness.Row("1002", OctorateMappingStatus.Confirmed, h.Properties[1]);
        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(0, batch.Channels[0].Counts.LeftForLater);
        Assert.Equal(2, batch.Channels[0].Counts.Connected);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));
    }

    [Fact]
    public async Task ExistingInventoryImport_ListingWithNoMappingRowCanStillBeLeftForLater_Test()
    {
        // The mapping knows only the first listing: the second one has no row at all. It is not a listing nobody can decide: it can be left for
        // later, and the others go on instead of waiting for it.
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var batch = await h.RunToRoomsAsync();
        h.Rows.Add(Harness.Row("1001", OctorateMappingStatus.Confirmed, h.Properties[0]));

        await using (var db = h.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(
                () => h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None));
            Assert.Equal("existing_inventory_mapping_pending", refused.Code);
        }

        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync)));

        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, ["1002"], CancellationToken.None);

        Assert.Equal(OctorateInventoryImportStatus.Completed, batch.Channels[0].Status);
        Assert.Equal(1, batch.Channels[0].Counts.LeftForLater);
        Assert.Equal(1, batch.Channels[0].Counts.Connected);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));

        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(1, batch.Channels[0].Counts.LeftForLater);
        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync), "502"));
    }

    [Fact]
    public async Task ExistingInventoryImport_NewInventoryOnlyWhenDeclaredAndNeverFromAnImport_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var declared = await h.AddPropertyAsync(OrgA);
        var mapped = h.Properties[0];

        await using var db = h.NewDb();
        var guard = new InventoryScenarioGuard(db);
        var service = h.Service(db);

        foreach (var scenario in new OctorateInventoryScenario?[] { null, OctorateInventoryScenario.OnOctorate, OctorateInventoryScenario.OnOtas })
        {
            if (scenario is { } declaredScenario)
                await service.SetIntentsAsync(CallerA, [new InventoryIntentAssignment(declared, declaredScenario, null)], CancellationToken.None);

            var refused = await Assert.ThrowsAsync<DomainConflictException>(() => guard.EnsureNewInventoryAsync(OrgA, declared, CancellationToken.None));
            Assert.Equal("octorate_existing_inventory_must_import", refused.Code);
            Assert.Equal(scenario, await guard.GetScenarioAsync(OrgA, declared, CancellationToken.None));
        }

        db.OctorateInventoryIntents.Add(new OctorateInventoryIntent
        {
            OrgId = OrgB,
            PropertyId = declared,
            Scenario = OctorateInventoryScenario.NewInventory,
            ActorUserId = UserB,
        });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainConflictException>(() => guard.EnsureNewInventoryAsync(OrgA, declared, CancellationToken.None));

        await service.SetIntentsAsync(CallerA, [new InventoryIntentAssignment(declared, OctorateInventoryScenario.NewInventory, null)], CancellationToken.None);
        await guard.EnsureNewInventoryAsync(OrgA, declared, CancellationToken.None);

        await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties([mapped]), CancellationToken.None);
        var changed = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.SetIntentsAsync(CallerA, [new InventoryIntentAssignment(mapped, OctorateInventoryScenario.NewInventory, null)], CancellationToken.None));
        Assert.Equal("inventory_intent_change_refused", changed.Code);
        Assert.Equal(OctorateInventoryScenario.OnOtas, await guard.GetScenarioAsync(OrgA, mapped, CancellationToken.None));
        await Assert.ThrowsAsync<DomainConflictException>(() => guard.EnsureNewInventoryAsync(OrgA, mapped, CancellationToken.None));
    }

    [Fact]
    public async Task ChannelAccessHandoff_ConfirmOnlyAfterTheCheckCouldNotTell_Test()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        var connections = new List<OctorateConnectionSummary> { new("501", "142", "1001", "H1", "WAITING", null) };
        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        client.Setup(c => c.GetOrgConnectionsAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(connections.ToArray()));
        var database = $"channel-access-{Guid.NewGuid():N}";
        AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options, NullTenantContext.Instance);
        OnboardingChannelAccessHandoff Handoff(AppDbContext db) => new(
            db,
            client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions
            {
                FrontendBaseUrl = PanelUrl,
                ExistingInventory = new OctorateExistingInventoryOptions { AccessCheckMaxAttempts = 2, AccessCheckBackoffSeconds = 1 },
            }),
            clock,
            NullLogger<OnboardingChannelAccessHandoff>.Instance);

        var batchId = Guid.NewGuid();
        await using var db = NewDb();
        var handoff = Handoff(db);
        var start = await handoff.StartAsync(OrgA, "142", new ChannelAccessResumePoint(batchId, "142"), CancellationToken.None);

        Assert.Equal(PanelUrl + "/user/channelmanager/edit.xhtml?portal=142", start.PanelUrl);
        Assert.Equal(PanelHandoffMode.ManagedWindow, start.HandoffMode);
        var step = await db.OnboardingPanelSteps.AsNoTracking().SingleAsync();
        Assert.Equal(start.StepId, step.Id);
        Assert.Equal(PanelStepKind.ChannelAccess, step.Kind);
        Assert.Equal("142", step.ChannelCode);
        Assert.Null(step.PropertyId);
        Assert.Null(step.HandoffNonceHash);
        Assert.Equal(WizardType.OnboardingActivation, step.ResumeWizardType);
        Assert.Equal(batchId, step.ResumeDraftEntityId);
        Assert.Equal(OnboardingChannelAccessHandoff.ResumeStepCode, step.ResumeStepCode);

        var early = await Assert.ThrowsAsync<DomainConflictException>(() => handoff.ConfirmAsync(OrgA, UserA, "142", CancellationToken.None));
        Assert.Equal("panel_step_verification_required", early.Code);
        Assert.Equal(ChannelAccessState.Waiting, (await handoff.GetAsync(OrgA, "142", verifyIfDue: true, CancellationToken.None)).State);
        client.Verify(c => c.GetOrgConnectionsAsync(It.IsAny<CancellationToken>()), Times.Never);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ChannelAccessState.Verifying, (await handoff.GetAsync(OrgA, "142", verifyIfDue: true, CancellationToken.None)).State);
        await Assert.ThrowsAsync<DomainConflictException>(() => handoff.ConfirmAsync(OrgA, UserA, "142", CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(1));
        var undecided = await handoff.GetAsync(OrgA, "142", verifyIfDue: true, CancellationToken.None);
        Assert.Equal(ChannelAccessState.NeedsConfirmation, undecided.State);
        Assert.False(undecided.IsComplete);
        await handoff.ConfirmAsync(OrgA, UserA, "142", CancellationToken.None);
        var confirmed = await handoff.GetAsync(OrgA, "142", verifyIfDue: false, CancellationToken.None);
        Assert.Equal(ChannelAccessState.Confirmed, confirmed.State);
        Assert.True(confirmed.IsComplete);
        Assert.Equal(UserA, (await db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).CompletedByUserId);

        await handoff.StartAsync(OrgA, "274", new ChannelAccessResumePoint(batchId, "274"), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        connections.Add(new OctorateConnectionSummary("502", "142", "1002", "H2", "ENABLED", true));
        Assert.Equal(ChannelAccessState.Verifying, (await handoff.GetAsync(OrgA, "274", verifyIfDue: true, CancellationToken.None)).State);
        clock.Advance(TimeSpan.FromMinutes(1));
        connections.Add(new OctorateConnectionSummary("503", "274", "1001", "H3", "ENABLED", true, clock.GetUtcNow().UtcDateTime));
        var verified = await handoff.GetAsync(OrgA, "274", verifyIfDue: true, CancellationToken.None);
        Assert.Equal(ChannelAccessState.Verified, verified.State);
        await handoff.ConfirmAsync(OrgA, UserA, "274", CancellationToken.None);

        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("https://panel.example.test/octobook?org=1"));
        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("http://panel.example.test/octobook"));
        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("https://user:secret@panel.example.test/octobook"));
        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("https://127.0.0.1/octobook"));
        Assert.Equal(PanelUrl, OnboardingChannelAccessHandoff.ParsePanelUrl(PanelUrl));
    }

    [Fact]
    public void ChannelAccessHandoff_TheAddressIsThePageOfThePortal_OnlyForACodeThatIsANumber_Test()
    {
        const string Booking = "https://admin.octorate.com/octobook/user/channelmanager/edit.xhtml?portal=142";
        Assert.Equal(Booking, OnboardingChannelAccessHandoff.PanelUrlOf("https://admin.octorate.com/octobook", "142"));
        Assert.Equal(Booking, OnboardingChannelAccessHandoff.PanelUrlOf("https://admin.octorate.com/octobook/", " 142 "));

        Assert.Equal("https://admin.octorate.com/octobook", OnboardingChannelAccessHandoff.PanelUrlOf("https://admin.octorate.com/octobook", "booking_xml"));
        Assert.Equal("https://admin.octorate.com/octobook", OnboardingChannelAccessHandoff.PanelUrlOf("https://admin.octorate.com/octobook", "1234567890123456789"));
        Assert.Equal("https://admin.octorate.com/octobook", OnboardingChannelAccessHandoff.PanelUrlOf("https://admin.octorate.com/octobook", null));

        Assert.Null(OnboardingChannelAccessHandoff.PanelUrlOf("https://admin.octorate.com/octobook?org=1", "142"));
        Assert.Null(OnboardingChannelAccessHandoff.PanelUrlOf(null, "142"));
    }

    [Fact]
    public async Task ChannelAccessHandoff_AConnectionAlreadyAuthorizedBeforeIsNoProofOfThisAccess_Test()
    {
        // The org already has a working connection of the channel for another property. That is not the access the person was sent to give:
        // the step must not be verified, and the import must not go on, because of it.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        var longBefore = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var connections = new List<OctorateConnectionSummary> { new("501", "142", "1001", "H1", "ENABLED", true, longBefore) };
        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        client.Setup(c => c.GetOrgConnectionsAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(connections.ToArray()));
        var database = $"channel-access-proof-{Guid.NewGuid():N}";
        AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options, NullTenantContext.Instance);
        OnboardingChannelAccessHandoff Handoff(AppDbContext db) => new(
            db,
            client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions
            {
                FrontendBaseUrl = PanelUrl,
                ExistingInventory = new OctorateExistingInventoryOptions { AccessCheckMaxAttempts = 6, AccessCheckBackoffSeconds = 1 },
            }),
            clock,
            NullLogger<OnboardingChannelAccessHandoff>.Instance);

        var property = Guid.NewGuid();
        var named = Guid.NewGuid();
        var whole = Guid.NewGuid();
        await using var db = NewDb();
        db.OctorateInventoryImportBatches.AddRange(
            new OctorateInventoryImportBatch
            {
                Id = named,
                OrgId = OrgA,
                ScopeKind = OctorateInventoryScopeKind.Properties,
                ScopePropertyIdsJson = JsonSerializer.Serialize(new[] { property }),
                CreatedByUserId = UserA,
            },
            new OctorateInventoryImportBatch
            {
                Id = whole,
                OrgId = OrgA,
                ScopeKind = OctorateInventoryScopeKind.AllInventory,
                CreatedByUserId = UserA,
            });
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgA,
            ConnectionId = Guid.NewGuid(),
            OctorateAccommodationId = "1002",
            PropertyId = property,
            Status = OctorateMappingStatus.Confirmed,
        });
        await db.SaveChangesAsync();
        var handoff = Handoff(db);

        async Task<ChannelAccessState> CheckAsync(string channel)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return (await handoff.GetAsync(OrgA, channel, verifyIfDue: true, CancellationToken.None)).State;
        }

        await handoff.StartAsync(OrgA, "142", new ChannelAccessResumePoint(named, "142"), CancellationToken.None);
        Assert.Equal(ChannelAccessState.Verifying, await CheckAsync("142"));
        connections.Add(new OctorateConnectionSummary("502", "142", "1009", "H9", "ENABLED", true, longBefore));
        Assert.Equal(ChannelAccessState.Verifying, await CheckAsync("142"));
        connections.Add(new OctorateConnectionSummary("503", "142", "1002", "H2", "ENABLED", true));
        Assert.Equal(ChannelAccessState.Verified, await CheckAsync("142"));

        await handoff.StartAsync(OrgA, "274", new ChannelAccessResumePoint(named, "274"), CancellationToken.None);
        connections.Add(new OctorateConnectionSummary("700", "274", "1010", "H10", "ENABLED", true, clock.GetUtcNow().UtcDateTime.AddDays(-10)));
        Assert.Equal(ChannelAccessState.Verifying, await CheckAsync("274"));
        clock.Advance(TimeSpan.FromMinutes(1));
        connections.Add(new OctorateConnectionSummary("701", "274", "1011", "H11", "ENABLED", true, clock.GetUtcNow().UtcDateTime));
        Assert.Equal(ChannelAccessState.Verified, await CheckAsync("274"));

        await handoff.StartAsync(OrgA, "235", new ChannelAccessResumePoint(whole, "235"), CancellationToken.None);
        connections.Add(new OctorateConnectionSummary("800", "235", "1002", "H20", "ENABLED", true));
        Assert.Equal(ChannelAccessState.Verifying, await CheckAsync("235"));
        Assert.Equal(ChannelAccessState.Verifying, await CheckAsync("235"));
        connections.Add(new OctorateConnectionSummary("801", "235", "1012", "H21", "ENABLED", true, clock.GetUtcNow().UtcDateTime));
        Assert.Equal(ChannelAccessState.Verified, await CheckAsync("235"));
    }

    [Fact]
    public void OctorateConnectionSummary_IsAuthorized_OnlyWhenTheConnectionIsEnabled_Test()
    {
        Assert.False(new OctorateConnectionSummary("1", "142", "520897", "FAKE-1", "WAITING", true).IsAuthorized);
        Assert.False(new OctorateConnectionSummary("2", "142", "520897", null, null, true, DateTime.UtcNow).IsAuthorized);
        Assert.False(new OctorateConnectionSummary("3", "142", "520897", null, "DISABLED", true).IsAuthorized);
        Assert.True(new OctorateConnectionSummary("4", "142", "520897", null, "ENABLED", false).IsAuthorized);
        Assert.True(new OctorateConnectionSummary("5", "142", "520897", null, " enabled ", null).IsAuthorized);
    }

    [Fact]
    public async Task ChannelAccessHandoff_TheConnectionCasaZenMadeIsTheProofOnceItIsEnabled_Test()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        var connections = new List<OctorateConnectionSummary>
        {
            new("900", "142", "520897", "FAKE-1", "WAITING", true),
            new("901", "142", "1009", "H9", "ENABLED", true, clock.GetUtcNow().UtcDateTime.AddDays(-30)),
        };
        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        client.Setup(c => c.GetOrgConnectionsAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(connections.ToArray()));
        var database = $"channel-access-collega-{Guid.NewGuid():N}";
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options, NullTenantContext.Instance);
        var handoff = new OnboardingChannelAccessHandoff(
            db,
            client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions
            {
                FrontendBaseUrl = PanelUrl,
                ExistingInventory = new OctorateExistingInventoryOptions { AccessCheckMaxAttempts = 6, AccessCheckBackoffSeconds = 1 },
            }),
            clock,
            NullLogger<OnboardingChannelAccessHandoff>.Instance);

        var whole = Guid.NewGuid();
        db.OctorateInventoryImportBatches.Add(new OctorateInventoryImportBatch
        {
            Id = whole,
            OrgId = OrgA,
            ScopeKind = OctorateInventoryScopeKind.AllInventory,
            CreatedByUserId = UserA,
            Channels = { new OctorateInventoryImportChannel { OrgId = OrgA, ChannelCode = "142", ConnectedAccommodationIdsJson = """["520897"]""" } },
        });
        await db.SaveChangesAsync();

        async Task<ChannelAccessState> CheckAsync()
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return (await handoff.GetAsync(OrgA, "142", verifyIfDue: true, CancellationToken.None)).State;
        }

        await handoff.StartAsync(OrgA, "142", new ChannelAccessResumePoint(whole, "142"), CancellationToken.None);
        Assert.Equal(ChannelAccessState.Verifying, await CheckAsync());
        Assert.Equal(ChannelAccessState.Verifying, await CheckAsync());

        connections[0] = connections[0] with { Status = "ENABLED" };
        Assert.Equal(ChannelAccessState.Verified, await CheckAsync());
    }

    [Fact]
    public async Task ExistingInventoryConnect_CreatesOneConnectionPerAccommodationWithTheIdOnThePortal_AndStartsTheStep_Test()
    {
        // Booking.com needs only the id of the property on it: the host types it, CasaZen creates the connection of the channel on each
        // accommodation of the batch with that id (and nothing else), records them and starts the access step. Another channel's connection on
        // the same accommodation does not count.
        var h = new Harness();
        await h.SeedAsync(verified: true);
        await h.PrepareConnectAsync();
        ExistingInventoryBatchView batch;
        await using (var db = h.NewDb())
            batch = await h.Service(db).CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties(h.Properties), CancellationToken.None);

        ExistingInventoryConnectTarget[] body = [new(" 1002 ", " 7654321 "), new("1001", "1234567")];
        await using (var db = h.NewDb())
            batch = await h.Service(db).ConnectChannelAsync(CallerA, batch.Id, "142", body, CancellationToken.None);

        Assert.Equal(
            new[] { ("1001", new OctorateConnectionCreate("142", "1234567", null, null)), ("1002", new OctorateConnectionCreate("142", "7654321", null, null)) },
            h.Created.ToArray());
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.GetOrgConnectionsAsync)));
        Assert.All(
            h.Client.Invocations.Select(i => i.Method.Name),
            name => Assert.Contains(name, new[] { nameof(IOctorateApiClient.GetOrgConnectionsAsync), nameof(IOctorateApiClient.CreateConnectionAsync) }));

        var channel = batch.Channels[0];
        Assert.Equal(new[] { "1001", "1002" }, channel.ConnectedAccommodationIds);
        Assert.Equal((OctorateInventoryImportStatus.AwaitingChannelAccess, ChannelAccessState.Waiting), (channel.Status, channel.AccessState));
        Assert.Equal(new ChannelAccessResumePoint(batch.Id, "142"), Assert.Single(h.Handoff.Started));
        await using (var db = h.NewDb())
            Assert.Equal(h.Handoff.StepId, (await db.OctorateInventoryImportChannels.AsNoTracking().SingleAsync()).PanelStepId);

        var json = JsonSerializer.Serialize(ExistingInventoryBatchDto.From(batch), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"connectedAccommodationIds\":[\"1001\",\"1002\"]", json, StringComparison.Ordinal);

        await using (var db = h.NewDb())
            await h.Service(db).ConnectChannelAsync(CallerA, batch.Id, "142", body, CancellationToken.None);
        Assert.Equal(2, h.Created.Count);

        h.Handoff.State = ChannelAccessState.Verified;
        var before = h.Client.Invocations.Count;
        await using (var db = h.NewDb())
            batch = await h.Service(db).ConnectChannelAsync(CallerA, batch.Id, "142", [new("1001", "999")], CancellationToken.None);
        Assert.Equal(before, h.Client.Invocations.Count);
        Assert.NotNull(batch.Channels[0].AccessCompletedAt);
        Assert.Equal(2, h.Handoff.Started.Count);
    }

    [Fact]
    public async Task ExistingInventoryConnect_NeverASecondConnectionOfTheChannelOnAnAccommodation_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        await h.PrepareConnectAsync();
        await h.AddMappingAsync(OrgA, "1003", OctorateMappingStatus.Confirmed, h.Properties[1]);
        h.Connections.Add(new OctorateConnectionSummary("601", "142", "1001", "H601", "WAITING", true));
        h.CreateFailures["1003"] = new OctorateRateLimitException(null);
        ExistingInventoryBatchView batch;
        await using (var db = h.NewDb())
            batch = await h.Service(db).CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties(h.Properties), CancellationToken.None);

        ExistingInventoryConnectTarget[] body = [new("1001", "111"), new("1002", "222"), new("1003", "333")];
        await using (var db = h.NewDb())
        {
            await Assert.ThrowsAsync<OctorateRateLimitException>(
                () => h.Service(db).ConnectChannelAsync(CallerA, batch.Id, "142", body, CancellationToken.None));
        }

        Assert.Equal(new[] { "1002", "1003" }, h.Created.Select(c => c.Accommodation).ToArray());
        Assert.Empty(h.Handoff.Started);
        await using (var db = h.NewDb())
        {
            var stored = await db.OctorateInventoryImportChannels.AsNoTracking().SingleAsync();
            Assert.Equal(new[] { "1001", "1002" }, ExistingInventoryImportService.ParseAccommodationIds(stored.ConnectedAccommodationIdsJson));
        }

        h.CreateFailures.Clear();
        h.ListShowsCreated = false;
        h.Connections.RemoveAll(c => c.AccommodationId == "1002");
        await using (var db = h.NewDb())
            batch = await h.Service(db).ConnectChannelAsync(CallerA, batch.Id, "142", body, CancellationToken.None);

        Assert.Equal(new[] { "1002", "1003", "1003" }, h.Created.Select(c => c.Accommodation).ToArray());
        Assert.Equal(new[] { "1001", "1002", "1003" }, batch.Channels[0].ConnectedAccommodationIds);
        Assert.Single(h.Handoff.Started);
    }

    [Fact]
    public async Task ExistingInventoryConnect_RefusesAPortalThatNeedsMore_AnIdThatIsNotOne_AndAnAccommodationOutsideTheBatch_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        await h.PrepareConnectAsync();
        await h.AddPortalAsync("274", tokenAuthentication: true);
        var outside = await h.AddPropertyAsync(OrgA);
        await h.AddMappingAsync(OrgA, "1004", OctorateMappingStatus.Confirmed, outside);
        await h.AddMappingAsync(OrgA, "1005", OctorateMappingStatus.Rejected);
        await h.AddMappingAsync(OrgB, "2001", OctorateMappingStatus.Confirmed);
        ExistingInventoryBatchView named;
        ExistingInventoryBatchView whole;
        await using (var db = h.NewDb())
        {
            var service = h.Service(db);
            named = await service.CreateBatchAsync(CallerA, ["142", "274"], ExistingInventoryScope.ForProperties(h.Properties), CancellationToken.None);
            whole = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.AllInventory, CancellationToken.None);
        }

        async Task<DomainRuleException> RefusedAsync(Guid batchId, string channel, IReadOnlyCollection<ExistingInventoryConnectTarget> body)
        {
            await using var db = h.NewDb();
            return await Assert.ThrowsAsync<DomainRuleException>(() => h.Service(db).ConnectChannelAsync(CallerA, batchId, channel, body, CancellationToken.None));
        }

        Assert.Equal("existing_inventory_connect_not_supported", (await RefusedAsync(named.Id, "274", [new("1001", "1234567")])).Code);

        Assert.Equal("existing_inventory_connect_empty", (await RefusedAsync(named.Id, "142", [])).Code);
        var tooMany = Enumerable.Range(0, ExistingInventoryImportService.MaxConnectionsPerRequest + 1).Select(i => new ExistingInventoryConnectTarget($"{3000 + i}", "1")).ToList();
        Assert.Equal("existing_inventory_connect_empty", (await RefusedAsync(named.Id, "142", tooMany)).Code);

        foreach (var bad in new[] { "", "   ", "12 34", "abc$", "id/../1", new string('9', ExistingInventoryImportService.HotelIdMaxLength + 1) })
        {
            var refused = await RefusedAsync(named.Id, "142", [new("1001", bad), new("1002", "7654321")]);
            Assert.Equal("existing_inventory_hotel_id_invalid", refused.Code);
            Assert.Equal(new[] { "1001" }, (IEnumerable<string>)refused.Extensions["accommodations"]!);
        }

        Assert.Equal("existing_inventory_hotel_id_invalid", (await RefusedAsync(named.Id, "142", [new("1001", "1"), new(" 1001", "2")])).Code);

        var unknown = await RefusedAsync(named.Id, "142", [new("1001", "1"), new("9999", "2"), new("1004", "3")]);
        Assert.Equal("existing_inventory_accommodation_unknown", unknown.Code);
        Assert.Equal(new[] { "1004", "9999" }, (IEnumerable<string>)unknown.Extensions["accommodations"]!);
        Assert.Equal("existing_inventory_accommodation_unknown", (await RefusedAsync(named.Id, "142", [new("", "1")])).Code);
        foreach (var notOfTheOrg in new[] { "1005", "2001" })
            Assert.Equal("existing_inventory_accommodation_unknown", (await RefusedAsync(whole.Id, "142", [new(notOfTheOrg, "1")])).Code);

        Assert.Empty(h.Client.Invocations);
        Assert.Empty(h.Handoff.Started);
        await using (var db = h.NewDb())
            Assert.All(await db.OctorateInventoryImportChannels.AsNoTracking().ToListAsync(), c => Assert.Null(c.ConnectedAccommodationIdsJson));
    }

    [Fact]
    public async Task ExistingInventoryConnect_TheControllerTakesTheBodyOfTheContract_Test()
    {
        var request = JsonSerializer.Deserialize<ConnectExistingInventoryChannelRequest>(
            """{ "accommodations": [ { "accommodationId": "520897", "hotelId": "1234567" } ] }""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var import = new Mock<IExistingInventoryImportService>(MockBehavior.Strict);
        var view = new ExistingInventoryBatchView(
            Guid.NewGuid(), OctorateInventoryScopeKind.AllInventory, null, [], OctorateInventoryImportStatus.AwaitingChannelAccess, null, DateTime.UtcNow, DateTime.UtcNow, null, []);
        import
            .Setup(i => i.ConnectChannelAsync(
                It.Is<ExistingInventoryCaller>(c => c.OrgId == OrgA && c.UserId == UserA),
                view.Id,
                "142",
                It.Is<IReadOnlyCollection<ExistingInventoryConnectTarget>>(a => a.SequenceEqual(new[] { new ExistingInventoryConnectTarget("520897", "1234567") })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(view);
        var controller = new OctorateExistingInventoryController(import.Object, new FixedOrg(OrgA))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = HostAuthorizationTestHarness.User(UserA, "PropertyOwner") },
            },
        };

        var answer = await controller.Connect(view.Id, "142", request, CancellationToken.None);

        Assert.Equal(view.Id, Assert.IsType<ExistingInventoryBatchDto>(Assert.IsType<OkObjectResult>(answer.Result).Value).Id);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task OctorateClient_D6Operations_OnlyTheFourDocumentedCallsAndNeverASuccessWithoutAnswer_Test()
    {
        var handler = new RecordingHandler();
        var scope = new OctorateOrgScope(NullTenantContext.Instance);
        var client = new OctorateApiClient(
            new HttpClient(handler),
            new StubTokens(),
            new OctorateRateLimiter(TimeProvider.System),
            scope,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" })
                .Build(),
            TimeProvider.System,
            NullLogger<OctorateApiClient>.Instance);

        using (scope.Use(OrgA))
        {
            handler.Body = """[{"productId": 376539, "externalId": 1154454, "portalConnection": 82043, "id": 536429}]""";
            var rooms = await client.ImportRoomsAsync("1001", "501", CancellationToken.None);
            Assert.Equal(OctorateD6Operation.ImportRooms, rooms.Operation);
            Assert.Equal(new OctorateImportedMapping("376539", "1154454"), Assert.Single(rooms.Mappings));
            await client.ImportReservationsAsync("1001", "501", CancellationToken.None);
            await client.ImportCalendarAsync("1001", "501", CancellationToken.None);
            await client.MarkConnectedAsync("1001", "501", CancellationToken.None);

            handler.Body = """[{"id": 501, "channelId": 142, "accommodationId": "1001", "hotelId": "H1", "status": "ENABLED", "userLogged": true, "loginAt": "2026-11-05T09:30:00Z", "username": "must-not-be-read", "password": "must-not-be-read"}, {"id": "abc", "channelId": 142, "accommodationId": "1"}, {"id": 9, "channelId": 142}]""";
            var connections = await client.GetOrgConnectionsAsync(CancellationToken.None);
            var connection = Assert.Single(connections);
            Assert.Equal(new OctorateConnectionSummary("501", "142", "1001", "H1", "ENABLED", true, new DateTime(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc)), connection);
            Assert.True(connection.IsAuthorized);
        }

        Assert.Equal(
            new[]
            {
                "POST /connect/rest/v1/connection/IMPORT_ROOMS/1001/501",
                "POST /connect/rest/v1/connection/IMPORT_RESERVATIONS/1001/501",
                "POST /connect/rest/v1/connection/IMPORT_CALENDAR/1001/501",
                "POST /connect/rest/v1/connection/MARK_CONNECTED/1001/501",
                "GET /connect/rest/v1/connection",
            },
            handler.Requests.Select(r => $"{r.Method} {r.Path}").ToArray());
        var read = handler.Requests[^1].Query;
        Assert.Contains("fields=", read, StringComparison.Ordinal);
        Assert.DoesNotContain("username", read, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", read, StringComparison.OrdinalIgnoreCase);

        var wrapper = new OctorateApiClient.OctorateAnswer(200, """{"additionalInfo": {"actionPerformed": "IMPORT_ROOMS", "connectionId": 501}, "data": [{"productId": 1, "externalId": 2}]}""");
        Assert.Single(OctorateApiClient.ReadD6Result(OctorateD6Operation.ImportRooms, "501", wrapper).Mappings);
        Assert.Empty(OctorateApiClient.ReadD6Result(OctorateD6Operation.MarkConnected, "501", new OctorateApiClient.OctorateAnswer(200, "[]")).Mappings);
        Assert.Empty(
            OctorateApiClient.ReadD6Result(
                OctorateD6Operation.ImportCalendar,
                "501",
                new OctorateApiClient.OctorateAnswer(200, """{"additionalInfo": {"actionPerformed": "import_calendar"}, "data": []}""")).Mappings);

        string[] notAnswers =
        [
            string.Empty,
            "ok",
            "\"done\"",
            "42",
            "null",
            "<html>403</html>",
            "{}",
            """{"error": "denied"}""",
            """{"data": [{"productId": 1, "externalId": 2}]}""",
            """{"additionalInfo": {"actionPerformed": "IMPORT_CALENDAR"}}""",
            """{"additionalInfo": {"actionPerformed": "IMPORT_CALENDAR"}, "data": {}}""",
            """{"additionalInfo": {}, "data": []}""",
            """{"additionalInfo": "IMPORT_CALENDAR", "data": []}""",
        ];
        foreach (var body in notAnswers)
        {
            Assert.Throws<OctorateServerException>(
                () => OctorateApiClient.ReadD6Result(OctorateD6Operation.ImportCalendar, "501", new OctorateApiClient.OctorateAnswer(200, body)));
        }

        Assert.Throws<OctorateServerException>(() => OctorateApiClient.ReadD6Result(OctorateD6Operation.ImportReservations, "501", wrapper));
        Assert.Throws<ArgumentException>(() => OctorateApiClient.CheckedConnectionId("5/../01"));

        handler.Body = "{}";
        using (scope.Use(OrgA))
            await Assert.ThrowsAsync<OctorateServerException>(() => client.ImportCalendarAsync("1001", "501", CancellationToken.None));
    }

    private sealed class Harness
    {
        private readonly string _database = $"existing-inventory-{Guid.NewGuid():N}";

        public Harness()
        {
            Client.Setup(c => c.GetOrgConnectionsAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(Connections.ToArray()));
            Client.Setup(c => c.ImportRoomsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string connection, CancellationToken _) => Act(OctorateD6Operation.ImportRooms, connection));
            Client.Setup(c => c.ImportReservationsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string connection, CancellationToken _) => Act(OctorateD6Operation.ImportReservations, connection));
            Client.Setup(c => c.ImportCalendarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string connection, CancellationToken _) => Act(OctorateD6Operation.ImportCalendar, connection));
            Client.Setup(c => c.MarkConnectedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string connection, CancellationToken _) => Act(OctorateD6Operation.MarkConnected, connection));

            Mappings.Setup(m => m.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<IReadOnlyList<ChannelMappingRow>>([.. Rows]));
            Mappings.Setup(m => m.RefreshAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<IReadOnlyList<ChannelMappingRow>>([.. Rows]));

            Bookings.Setup(b => b.ImportAllAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<BookingImportOptions?>()))
                .Returns(() => Task.FromResult(new ImportResult(1, 0, 0, [], ImportOutcome.Completed)));
        }

        public Mock<IOctorateApiClient> Client { get; } = new(MockBehavior.Strict);

        public Mock<IChannelMappingService> Mappings { get; } = new(MockBehavior.Strict);

        public Mock<IBookingImportService> Bookings { get; } = new(MockBehavior.Strict);

        public StubHandoff Handoff { get; } = new();

        public List<OctorateConnectionSummary> Connections { get; } = [];

        public List<ChannelMappingRow> Rows { get; } = [];

        public Dictionary<(OctorateD6Operation Operation, string Connection), Exception> Failures { get; } = [];

        public OctorateExistingInventoryOptions Capabilities { get; } = new();

        public List<Guid> Properties { get; } = [];

        private static readonly HonourCancellation CancellationInterceptor = new();

        public Action<OctorateD6Operation, string>? AfterAnswer { get; set; }

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).AddInterceptors(CancellationInterceptor).Options, NullTenantContext.Instance);

        public ExistingInventoryImportService Service(
            AppDbContext db,
            IHostScopeResolver? scope = null,
            ILogger<ExistingInventoryImportService>? logger = null) =>
            new(
                db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                Mappings.Object,
                Bookings.Object,
                Handoff,
                scope ?? new FakeScope(OrgA),
                Microsoft.Extensions.Options.Options.Create(new OctorateOptions
                {
                    FrontendBaseUrl = PanelUrl,
                    ExistingInventory = Capabilities,
                }),
                TimeProvider.System,
                logger ?? NullLogger<ExistingInventoryImportService>.Instance);

        public async Task SeedAsync(bool verified)
        {
            await using (var db = NewDb())
            {
                db.MarketplacePortals.Add(new MarketplacePortal
                {
                    PortalCode = "142",
                    Name = "Portal 142",
                    Category = MarketplacePortalCategory.Ota,
                    Enabled = true,
                    IsListed = true,
                });
                await db.SaveChangesAsync();
            }

            Properties.Add(await AddPropertyAsync(OrgA));
            Properties.Add(await AddPropertyAsync(OrgA));
            Connections.AddRange([Conn("501", "1001"), Conn("502", "1002"), Conn("503", "1001", "274")]);
            Handoff.State = ChannelAccessState.Verified;
            if (verified)
            {
                Capabilities.Channels["142"] = new OctorateExistingInventoryChannelOptions
                {
                    ImportRooms = true,
                    ImportReservations = true,
                    ImportCalendar = true,
                    MarkConnected = true,
                };
            }
        }

        public List<(string Accommodation, OctorateConnectionCreate Request)> Created { get; } = [];

        public Dictionary<string, Exception> CreateFailures { get; } = [];

        public bool ListShowsCreated { get; set; } = true;

        /// <summary>
        /// The ground of «Collega»: the portal «142» needs only the id of the property on it, the two accommodations of the batch are confirmed to
        /// its two properties, «142» has no connection yet (the one of «274» on 1001 stays), the access was never started, and Octorate creates.
        /// </summary>
        public async Task PrepareConnectAsync()
        {
            await using (var db = NewDb())
            {
                var portal = await db.MarketplacePortals.SingleAsync(p => p.PortalCode == "142");
                portal.RequiresHotelId = true;
                portal.Creatable = true;
                await db.SaveChangesAsync();
            }

            await AddMappingAsync(OrgA, "1001", OctorateMappingStatus.Confirmed, Properties[0]);
            await AddMappingAsync(OrgA, "1002", OctorateMappingStatus.Confirmed, Properties[1]);
            Connections.RemoveAll(c => c.ChannelId == "142");
            Handoff.State = ChannelAccessState.NotStarted;
            Client.Setup(c => c.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<OctorateConnectionCreate>(), It.IsAny<CancellationToken>()))
                .Returns((string accommodation, OctorateConnectionCreate request, CancellationToken _) =>
                {
                    Created.Add((accommodation, request));
                    if (CreateFailures.TryGetValue(accommodation, out var failure))
                        return Task.FromException<OctorateConnectionInfo>(failure);

                    var id = "9" + accommodation;
                    if (ListShowsCreated)
                        Connections.Add(new OctorateConnectionSummary(id, request.PortalCode, accommodation, request.HotelId, "WAITING", true));

                    return Task.FromResult(new OctorateConnectionInfo(id, request.PortalCode, "WAITING", null, null, UserLogged: true));
                });
        }

        public async Task AddMappingAsync(Guid orgId, string accommodation, OctorateMappingStatus status, Guid? propertyId = null)
        {
            await using var db = NewDb();
            db.OctorateListingMappings.Add(new OctorateListingMapping
            {
                OrgId = orgId,
                ConnectionId = Guid.NewGuid(),
                OctorateAccommodationId = accommodation,
                PropertyId = propertyId,
                Status = status,
            });
            await db.SaveChangesAsync();
        }

        public async Task AddPortalAsync(string code, bool tokenAuthentication)
        {
            await using var db = NewDb();
            db.MarketplacePortals.Add(new MarketplacePortal
            {
                PortalCode = code,
                Name = "Portal " + code,
                Category = MarketplacePortalCategory.Ota,
                Enabled = true,
                IsListed = true,
                Creatable = true,
                RequiresHotelId = true,
                RequiresTokenAuthentication = tokenAuthentication,
            });
            await db.SaveChangesAsync();
        }

        public async Task<ExistingInventoryBatchView> RunToRoomsAsync()
        {
            await using var db = NewDb();
            var service = Service(db);
            var batch = await service.CreateBatchAsync(CallerA, ["142"], ExistingInventoryScope.ForProperties(Properties), CancellationToken.None);
            await service.StartChannelAccessAsync(CallerA, batch.Id, "142", CancellationToken.None);
            return await service.RunAsync(CallerA, batch.Id, CancellationToken.None);
        }

        public async Task<Guid> AddPropertyAsync(Guid orgId)
        {
            await using var db = NewDb();
            var property = new Property
            {
                OrgId = orgId,
                OwnerId = "auth0|owner",
                Name = "Casa " + Guid.NewGuid().ToString("N")[..6],
                Address = "Via Prova 1",
                City = "Como",
                MaxGuests = 4,
            };
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            return property.Id;
        }

        public int Calls(string method) => Client.Invocations.Count(i => i.Method.Name == method);

        public int Calls(string method, string connection) =>
            Client.Invocations.Count(i => i.Method.Name == method && Equals(i.Arguments[1], connection));

        public static OctorateConnectionSummary Conn(string id, string accommodation, string channel = "142") =>
            new(id, channel, accommodation, "H" + id, "ENABLED", true);

        public static ChannelMappingRow Row(string accommodation, OctorateMappingStatus status, Guid? propertyId = null, DateTime? confirmedAt = null) =>
            new(
                Guid.NewGuid(),
                accommodation,
                "Listing " + accommodation,
                null,
                null,
                null,
                [],
                null,
                status,
                0,
                propertyId is { } id ? new ChannelMappingProperty(id, "Casa", "Via Prova 1", "Como") : null,
                [],
                [],
                confirmedAt);

        private Task<OctorateConnectionActionResult> Act(OctorateD6Operation operation, string connection)
        {
            if (Failures.TryGetValue((operation, connection), out var failure))
                return Task.FromException<OctorateConnectionActionResult>(failure);

            AfterAnswer?.Invoke(operation, connection);
            return Task.FromResult(new OctorateConnectionActionResult(operation, connection, [new OctorateImportedMapping("p" + connection, "e" + connection)]));
        }
    }

    private sealed class HonourCancellation : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class StubHandoff : IChannelAccessHandoff
    {
        public ChannelAccessState State { get; set; } = ChannelAccessState.NotStarted;

        public Guid StepId { get; } = Guid.NewGuid();

        public List<ChannelAccessResumePoint> Started { get; } = [];

        public Task<ChannelAccessHandoffStart> StartAsync(Guid orgId, string channelCode, ChannelAccessResumePoint resume, CancellationToken ct)
        {
            Started.Add(resume);
            if (State == ChannelAccessState.NotStarted)
                State = ChannelAccessState.Waiting;

            return Task.FromResult(new ChannelAccessHandoffStart(StepId, PanelHandoffMode.ManagedWindow, PanelUrl));
        }

        public Task<ChannelAccessSnapshot> GetAsync(Guid orgId, string channelCode, bool verifyIfDue, CancellationToken ct) =>
            Task.FromResult(new ChannelAccessSnapshot(State == ChannelAccessState.NotStarted ? null : StepId, State));

        public Task ConfirmAsync(Guid orgId, string userId, string channelCode, CancellationToken ct)
        {
            State = ChannelAccessState.Confirmed;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScope(Guid orgId, bool orgWide = true, params Guid[] reachable) : IHostScopeResolver
    {
        public Task<HostScope?> ResolveAsync(string userId, IReadOnlySet<string> jwtRoles, Guid org, CancellationToken cancellationToken = default) =>
            Task.FromResult<HostScope?>(org != orgId ? null : orgWide ? new HostScope(org) : new HostScope(org, GrantedToUserId: userId));

        public Task<bool> CanReachPropertyAsync(
            string userId,
            IReadOnlySet<string> jwtRoles,
            Guid org,
            Guid? propertyId,
            string? propertyOwnerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(org == orgId && (orgWide || (propertyId is { } id && reachable.Contains(id))));
    }

    private sealed class FixedOrg(Guid orgId) : Casazen.Web.Infrastructure.IOrgContextResolver
    {
        public Task<Guid?> GetOrProvisionOrgIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(orgId);
    }

    private sealed class StubTokens : IOctorateTokenService
    {
        public Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct) => Task.FromResult("stub-access");

        public Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No token is refused in these tests.");

        public Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(Guid orgId, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetPartnerAccessTokenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");

        public Task<string> RefreshRejectedPartnerTokenAsync(string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "[]";

        public List<(string Method, string Path, string Query)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                Lines.AddRange(pairs.Select(pair => $"{pair.Key}={pair.Value}"));

            if (exception is not null)
                Lines.Add(exception.ToString());
        }
    }
}
