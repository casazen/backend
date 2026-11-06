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
using Casazen.Web.DTOs;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>The only calls of the Octorate client an import may make: the read of the connections and the four operations of the D6 sequence.</summary>
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

    // ─── Proofs ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistingInventoryImport_NeverCreatesOrPublishesListing_Test()
    {
        // Every state the import goes through, every kind of failure and the retries after them: the Octorate client is asked for the
        // connections and for the four operations of the D6 sequence and for nothing else, the mapping is only read, and no property is made.
        var harnesses = new List<Harness>();

        // 1. No operation verified: nothing is called but the read of the connections; the step stays guided.
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

        // 3. A failure of the channel (550) is a Failed channel with a stable code, and so is an unknown result; both are retried, never replaced.
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
            view = await failing.Service(db).ContinueAsync(CallerA, view.Id, null, CancellationToken.None);
        Assert.Equal(OctorateInventoryImportStatus.Completed, view.Status);

        // 4. The access is given but Octorate shows no connection: nothing is created to make one.
        var missing = new Harness();
        harnesses.Add(missing);
        await missing.SeedAsync(verified: true);
        missing.Connections.Clear();
        view = await missing.RunToRoomsAsync();
        Assert.Equal(OctorateInventoryErrorCodes.ChannelConnectionNotFound, view.Channels[0].ErrorCode);

        // 5. The whole sequence, to the end.
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

            // The mapping is read and refreshed; it is never confirmed, rejected, imported as a property or set by the import.
            Assert.All(
                harness.Mappings.Invocations.Select(i => i.Method.Name).Distinct(),
                name => Assert.Contains(name, new[] { nameof(IChannelMappingService.ListAsync), nameof(IChannelMappingService.RefreshAsync) }));
            Assert.All(
                harness.Bookings.Invocations.Select(i => i.Method.Name).Distinct(),
                name => Assert.Equal(nameof(IBookingImportService.ImportAllAsync), name));

            // No property is made by the import (the properties are the two the test seeded).
            await using var db = harness.NewDb();
            Assert.Equal(2, await db.Properties.CountAsync());
        }

        // The sequence has the four operations and no other: there is no member that could create or publish a listing.
        Assert.Equal(
            new[] { "ImportCalendar", "ImportReservations", "ImportRooms", "MarkConnected" },
            Enum.GetNames<OctorateD6Operation>().Order().ToArray());

        // The service depends on no creator or publisher: what it holds is the client, the mapping (read and refreshed), the bookings import and the handoff.
        var dependencies = typeof(ExistingInventoryImportService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name).ToArray();
        Assert.DoesNotContain(
            dependencies,
            name => name.Contains("Creator", StringComparison.Ordinal) || name.Contains("Publish", StringComparison.Ordinal) || name.Contains("ContentPush", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingInventoryImport_SecretsNeverEnterCasaZen_Test()
    {
        // No type of the request, of the state or of the answer has a member for a username, a password, a cookie or a token of an OTA.
        Type[] types =
        [
            typeof(OctorateInventoryIntent), typeof(OctorateInventoryImportBatch), typeof(OctorateInventoryImportChannel), typeof(OctorateInventoryImportItem),
            typeof(CreateExistingInventoryBatchRequest), typeof(ContinueExistingInventoryRequest), typeof(InventoryIntentsRequest), typeof(InventoryIntentAssignmentRequest),
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

        // The address of the panel is in the answer of the start of the access and nowhere else.
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

        // The address returned carries no organization, property or person data, and it is saved before it is returned.
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

            // From now on the address is not in any answer, nor in a log line.
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

        // A property of another organization is the same 404 as one that does not exist, alone or among the caller's own: nothing is stored.
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

        // A group of the mixed portfolio is the org's own: the group of another org does not exist for the caller, and a group never takes in
        // the property of another org.
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
            // The org B's own row is untouched and org A has no intent for the foreign property.
            Assert.Empty(await db.OctorateInventoryIntents.Where(i => i.OrgId == OrgA && i.PropertyId == theirs).ToListAsync());
            Assert.Single(await db.OctorateInventoryIntents.Where(i => i.OrgId == OrgB).ToListAsync());
        }

        // A caller who reaches only some properties cannot take the whole inventory, nor a property it was not given.
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

            // A batch of the whole inventory is not seen by such a caller, and a batch of org A is not seen by org B.
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
    public async Task ExistingInventoryImport_ResumeIsIdempotent_Test()
    {
        var h = new Harness();
        await h.SeedAsync(verified: true);
        var properties = h.Properties;

        // The first batch: the access is given, the rooms are imported, and a restart of the worker (a new context and service) changes nothing.
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

        // The person confirms both listings; the calendar of the second one fails: the first one is connected, the second one is not.
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

        // A restart and the same command: only what has no stamp is asked again, and only the second listing is marked.
        h.Failures.Clear();
        await using (var db = h.NewDb())
            first = await h.Service(db).ContinueAsync(CallerA, first.Id, null, CancellationToken.None);
        Assert.Equal(OctorateInventoryImportStatus.Completed, first.Status);
        Assert.Equal(2, first.Channels[0].Counts.Connected);
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportRoomsAsync)));
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync)));
        Assert.Equal(3, h.Calls(nameof(IOctorateApiClient.ImportCalendarAsync)));
        Assert.Equal(2, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync)));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));

        // Repeating it again asks Octorate for nothing.
        var before = h.Client.Invocations.Count;
        await using (var db = h.NewDb())
            first = await h.Service(db).ContinueAsync(CallerA, first.Id, null, CancellationToken.None);
        Assert.Equal(before, h.Client.Invocations.Count);
        Assert.Equal(OctorateInventoryImportStatus.Completed, first.Status);

        // A second batch for the same channel takes the stamps of the connections: no operation is asked for the same item twice.
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

    // ─── The rules around them ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistingInventoryImport_RoomImportStoresEachConnectionAsANewRow_Test()
    {
        // The items of a channel the context already tracks are inserted, not updated: a row that is not there cannot be updated.
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

        // One listing is confirmed, the other is ambiguous: nothing goes on until somebody decides, and nothing was asked for the reservations.
        h.Rows.AddRange([Harness.Row("1001", OctorateMappingStatus.Confirmed, h.Properties[0]), Harness.Row("1002", OctorateMappingStatus.Ambiguous)]);
        await using (var db = h.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(
                () => h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None));
            Assert.Equal("existing_inventory_mapping_pending", refused.Code);
        }

        Assert.Equal(0, h.Calls(nameof(IOctorateApiClient.ImportReservationsAsync)));

        // Left for later: the other listing goes on to the end, the left one stays as it is (write-blocked by the mapping, not connected).
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

        // Once it is confirmed it goes on by itself on the next command and is no longer «left».
        h.Rows[1] = Harness.Row("1002", OctorateMappingStatus.Confirmed, h.Properties[1]);
        await using (var db = h.NewDb())
            batch = await h.Service(db).ContinueAsync(CallerA, batch.Id, null, CancellationToken.None);

        Assert.Equal(0, batch.Channels[0].Counts.LeftForLater);
        Assert.Equal(2, batch.Channels[0].Counts.Connected);
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "501"));
        Assert.Equal(1, h.Calls(nameof(IOctorateApiClient.MarkConnectedAsync), "502"));
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

        // No declaration, a declaration of existing inventory, and a declaration of another org: the creation is refused (409).
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

        // Only the explicit choice of the person opens it, and only for a property no import concerns.
        await service.SetIntentsAsync(CallerA, [new InventoryIntentAssignment(declared, OctorateInventoryScenario.NewInventory, null)], CancellationToken.None);
        await guard.EnsureNewInventoryAsync(OrgA, declared, CancellationToken.None);

        // A property that an import batch covers cannot be moved to «new»: it is never an automatic recovery.
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

        // The hand-over and the resume point are stored before the address is returned; no nonce, no credential.
        Assert.Equal(PanelUrl, start.PanelUrl);
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

        // Before any check, and while the checks go on, the person cannot confirm.
        var early = await Assert.ThrowsAsync<DomainConflictException>(() => handoff.ConfirmAsync(OrgA, UserA, "142", CancellationToken.None));
        Assert.Equal("panel_step_verification_required", early.Code);
        Assert.Equal(ChannelAccessState.Waiting, (await handoff.GetAsync(OrgA, "142", verifyIfDue: true, CancellationToken.None)).State);
        client.Verify(c => c.GetOrgConnectionsAsync(It.IsAny<CancellationToken>()), Times.Never);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ChannelAccessState.Verifying, (await handoff.GetAsync(OrgA, "142", verifyIfDue: true, CancellationToken.None)).State);
        await Assert.ThrowsAsync<DomainConflictException>(() => handoff.ConfirmAsync(OrgA, UserA, "142", CancellationToken.None));

        // The last check without proof stores «could not tell»: only now the person may confirm.
        clock.Advance(TimeSpan.FromMinutes(1));
        var undecided = await handoff.GetAsync(OrgA, "142", verifyIfDue: true, CancellationToken.None);
        Assert.Equal(ChannelAccessState.NeedsConfirmation, undecided.State);
        Assert.False(undecided.IsComplete);
        await handoff.ConfirmAsync(OrgA, UserA, "142", CancellationToken.None);
        var confirmed = await handoff.GetAsync(OrgA, "142", verifyIfDue: false, CancellationToken.None);
        Assert.Equal(ChannelAccessState.Confirmed, confirmed.State);
        Assert.True(confirmed.IsComplete);
        Assert.Equal(UserA, (await db.OnboardingPanelSteps.AsNoTracking().SingleAsync()).CompletedByUserId);

        // Another channel: an authorized connection of THIS channel is the proof, one of another channel is not, and then there is nothing to confirm.
        await handoff.StartAsync(OrgA, "274", new ChannelAccessResumePoint(batchId, "274"), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        connections.Add(new OctorateConnectionSummary("502", "142", "1002", "H2", "ENABLED", true));
        Assert.Equal(ChannelAccessState.Verifying, (await handoff.GetAsync(OrgA, "274", verifyIfDue: true, CancellationToken.None)).State);
        clock.Advance(TimeSpan.FromMinutes(1));
        connections.Add(new OctorateConnectionSummary("503", "274", "1001", "H3", null, true));
        var verified = await handoff.GetAsync(OrgA, "274", verifyIfDue: true, CancellationToken.None);
        Assert.Equal(ChannelAccessState.Verified, verified.State);
        await handoff.ConfirmAsync(OrgA, UserA, "274", CancellationToken.None);

        // The address is only ever the configured one: no query, https, a named host.
        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("https://panel.example.test/octobook?org=1"));
        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("http://panel.example.test/octobook"));
        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("https://user:secret@panel.example.test/octobook"));
        Assert.Null(OnboardingChannelAccessHandoff.ParsePanelUrl("https://127.0.0.1/octobook"));
        Assert.Equal(PanelUrl, OnboardingChannelAccessHandoff.ParsePanelUrl(PanelUrl));
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

        // The calls: a POST to /connection/{ACTION}/{accommodation}/{connection} for each of the four, and a read of the connections that asks only ids and state.
        using (scope.Use(OrgA))
        {
            handler.Body = """[{"productId": 376539, "externalId": 1154454, "portalConnection": 82043, "id": 536429}]""";
            var rooms = await client.ImportRoomsAsync("1001", "501", CancellationToken.None);
            Assert.Equal(OctorateD6Operation.ImportRooms, rooms.Operation);
            Assert.Equal(new OctorateImportedMapping("376539", "1154454"), Assert.Single(rooms.Mappings));
            await client.ImportReservationsAsync("1001", "501", CancellationToken.None);
            await client.ImportCalendarAsync("1001", "501", CancellationToken.None);
            await client.MarkConnectedAsync("1001", "501", CancellationToken.None);

            handler.Body = """[{"id": 501, "channelId": 142, "accommodationId": "1001", "hotelId": "H1", "status": "ENABLED", "userLogged": true, "username": "must-not-be-read", "password": "must-not-be-read"}, {"id": "abc", "channelId": 142, "accommodationId": "1"}, {"id": 9, "channelId": 142}]""";
            var connections = await client.GetOrgConnectionsAsync(CancellationToken.None);
            var connection = Assert.Single(connections);
            Assert.Equal(new OctorateConnectionSummary("501", "142", "1001", "H1", "ENABLED", true), connection);
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

        // An answer that is not a list (or the documented wrapper for the same action) is an unknown result, never a success.
        var wrapper = new OctorateApiClient.OctorateAnswer(200, """{"additionalInfo": {"actionPerformed": "IMPORT_ROOMS", "connectionId": 501}, "data": [{"productId": 1, "externalId": 2}]}""");
        Assert.Single(OctorateApiClient.ReadD6Result(OctorateD6Operation.ImportRooms, "501", wrapper).Mappings);
        Assert.Empty(OctorateApiClient.ReadD6Result(OctorateD6Operation.MarkConnected, "501", new OctorateApiClient.OctorateAnswer(200, "[]")).Mappings);
        foreach (var body in new[] { string.Empty, "ok", "\"done\"", "42", "null", "<html>403</html>" })
        {
            Assert.Throws<OctorateServerException>(
                () => OctorateApiClient.ReadD6Result(OctorateD6Operation.ImportCalendar, "501", new OctorateApiClient.OctorateAnswer(200, body)));
        }

        Assert.Throws<OctorateServerException>(() => OctorateApiClient.ReadD6Result(OctorateD6Operation.ImportReservations, "501", wrapper));
        Assert.Throws<ArgumentException>(() => OctorateApiClient.CheckedConnectionId("5/../01"));
    }

    // ─── Harness ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An org with two properties, two connections of the channel «142» (and one of another channel), a strict Octorate client and the doubles around the service.</summary>
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

        /// <summary>The two properties of org A the batches are about.</summary>
        public List<Guid> Properties { get; } = [];

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

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

        /// <summary>The portal «142» in the catalog, two properties of org A, two connections of «142» and one of «274», the access given.</summary>
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

        /// <summary>Creates the batch for the two properties, gets the access (already given) and runs the rooms; the view after the run.</summary>
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

        public static ChannelMappingRow Row(string accommodation, OctorateMappingStatus status, Guid? propertyId = null) =>
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
                null);

        private Task<OctorateConnectionActionResult> Act(OctorateD6Operation operation, string connection) =>
            Failures.TryGetValue((operation, connection), out var failure)
                ? Task.FromException<OctorateConnectionActionResult>(failure)
                : Task.FromResult(new OctorateConnectionActionResult(operation, connection, [new OctorateImportedMapping("p" + connection, "e" + connection)]));
    }

    /// <summary>The panel step of the access: the state is the test's, the start is recorded.</summary>
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

    /// <summary>The reach of a caller: the whole org of <paramref name="orgId"/>, or only the properties it was given; nothing in another org.</summary>
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

    /// <summary>The tokens of a stub: a fixed one for every org and for the partner.</summary>
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

    /// <summary>Answers every call with the same body and keeps the method, the path and the query of each request.</summary>
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

    /// <summary>Keeps every formatted line and every named value a log call carried.</summary>
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
