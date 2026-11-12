using System.Net;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F02/US01/T01: the seven operations on a connection call the endpoint of the OpenAPI of 2026-10-31 with its path and body, read the answer
/// they document and raise the typed errors of ADR-023 (the 550 of the partner is its own). Nothing here is Octorate: the handler is a stub that keeps
/// what the client sent and answers what the test queued. The sandbox has no channel connected, so these tests are the proof of the shapes in CasaZen.
/// </summary>
public class OctorateConnectionOperationsTests
{
    private const string Acc = "585492";
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a91");

    [Fact]
    public async Task ListConnectionsAsync_AsksOnlyForTheSafeMembers_AndLeavesOutAConnectionOfAnotherAccommodation()
    {
        var h = new OctorateConnectionClientHarness();
        h.Answer(
            HttpStatusCode.OK,
            """
            [
              { "id": 7001, "channelId": 142, "accommodationId": 585492, "status": "ENABLED", "userLogged": true, "loginAt": 1793000000000, "updateCalendar": true, "pullReservations": false,
                "lastPortalAcceptLog": { "time": "2026-11-05T10:00:00Z", "success": false, "message": "Property not accepted yet" }, "username": "x", "password": "y" },
              { "id": 7002, "channelId": 274, "accommodationId": 585492, "status": "WAITING", "userLogged": false, "lastPortalAcceptLog": "2026-11-04T09:30:00Z" },
              { "id": 7003, "channelId": 142, "accommodationId": 999999, "status": "ENABLED" },
              { "channelId": 142, "accommodationId": 585492 }
            ]
            """);

        OctorateConnectionInfo[] listed;
        using (h.Scope.Use(OrgA))
            listed = await h.Client.ListConnectionsAsync(Acc, CancellationToken.None);

        var request = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection", request.Uri.GetLeftPart(UriPartial.Path));
        Assert.Contains($"accommodation={Acc}", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("fields=", request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("username", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", request.Uri.Query, StringComparison.OrdinalIgnoreCase);

        // The connection of another accommodation and the item with no id are left out.
        Assert.Equal(["7001", "7002"], listed.Select(c => c.ConnectionId));

        var enabled = listed[0];
        Assert.Equal(("142", "ENABLED", true, true), (enabled.PortalCode, enabled.Status, enabled.IsConnected, enabled.PanelAccessDone));
        Assert.Equal((true, false), (enabled.UpdateCalendar, enabled.PullReservations));
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), enabled.LastUpdate);
        Assert.Equal("Property not accepted yet", enabled.LastError);

        // A bare moment is a moment and never an error; a connection that is waiting is not an access given.
        var waiting = listed[1];
        Assert.Equal((false, false, false), (waiting.IsConnected, waiting.PanelAccessDone, waiting.LastError is not null));
        Assert.Equal(new DateTime(2026, 11, 4, 9, 30, 0, DateTimeKind.Utc), waiting.LastUpdate);
    }

    [Theory]
    [InlineData("""{ "error": "invalid request", "type": "ApiException" }""")]
    [InlineData("{}")]
    [InlineData("""{ "data": "x" }""")]
    [InlineData("\"nothing\"")]
    [InlineData("""[ { "channelId": 142, "accommodationId": 585492, "status": "ENABLED" } ]""")]
    [InlineData("""[ { "id": 7001, "accommodationId": 585492, "status": "ENABLED" } ]""")]
    [InlineData("""{ "data": [ { "status": "ENABLED" }, { "foo": 1 } ] }""")]
    public async Task ListConnectionsAsync_AnAnswerThatIsNotAListOfConnections_IsAFailureOfTheRead_NeverAnEmptyList(string body)
    {
        // An error that came with HTTP 200, a wrapper without its list, items none of which can be read: not an empty list, so no connection is taken for gone because of it.
        var h = new OctorateConnectionClientHarness();
        h.Answer(HttpStatusCode.OK, body);

        using (h.Scope.Use(OrgA))
        {
            var failure = await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.ListConnectionsAsync(Acc, CancellationToken.None));
            Assert.Equal(("octorate_unavailable", 200), (failure.Code, failure.OctorateStatusCode));
        }
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("""{ "data": [], "page": { "number": 0 } }""", 0)]
    [InlineData("""[ { "id": 7003, "channelId": 142, "accommodationId": 999999, "status": "ENABLED" } ]""", 0)]
    [InlineData("""{ "id": 7001, "channelId": 142, "accommodationId": 585492, "status": "ENABLED" }""", 1)]
    [InlineData("""{ "data": { "id": 7001, "channelId": 142, "status": "ENABLED" } }""", 1)]
    [InlineData("""{ "data": [ { "id": 7001, "channelId": 142, "status": "ENABLED" }, { "status": "ENABLED" } ] }""", 1)]
    public async Task ListConnectionsAsync_AListThatCanBeRead_IsReturned_AlsoWhenItIsEmptyOrHoldsAnItemThatIsNotAConnection(string body, int expected)
    {
        var h = new OctorateConnectionClientHarness();
        h.Answer(HttpStatusCode.OK, body);

        using (h.Scope.Use(OrgA))
            Assert.Equal(expected, (await h.Client.ListConnectionsAsync(Acc, CancellationToken.None)).Length);
    }

    [Fact]
    public async Task CreateConnectionAsync_SendsThePortalTheAccessDataAndAConnectionThatReadsAndWritesNothingYet()
    {
        var h = new OctorateConnectionClientHarness();
        h.Answer(HttpStatusCode.OK, """{ "id": 7001, "channelId": 142, "accommodationId": 585492, "status": "WAITING" }""");

        OctorateConnectionInfo created;
        using (h.Scope.Use(OrgA))
            created = await h.Client.CreateConnectionAsync(Acc, new OctorateConnectionCreate("142", " H-4471 ", " user ", "p w"), CancellationToken.None);

        var request = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection/{Acc}", request.Uri.ToString());
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(142, root.GetProperty("channelId").GetInt64());
        Assert.Equal(142, root.GetProperty("portal").GetProperty("id").GetInt64());
        Assert.Equal("H-4471", root.GetProperty("hotelId").GetString());
        Assert.Equal("user", root.GetProperty("username").GetString());
        Assert.Equal("p w", root.GetProperty("password").GetString());

        // D6: nothing is pulled from the channel and nothing is pushed to it before the rooms are mapped; no content is published.
        Assert.False(root.GetProperty("updateCalendar").GetBoolean());
        Assert.False(root.GetProperty("pullReservations").GetBoolean());
        Assert.True(root.GetProperty("pullMappedOnly").GetBoolean());
        Assert.False(root.GetProperty("content").GetBoolean());
        Assert.Equal(("7001", "142"), (created.ConnectionId, created.PortalCode));

        // What the portal did not ask for is not sent at all.
        var bare = new OctorateConnectionClientHarness();
        bare.Answer(HttpStatusCode.OK, """{ "id": 7002, "channelId": 274, "status": "WAITING" }""");
        using (bare.Scope.Use(OrgA))
            await bare.Client.CreateConnectionAsync(Acc, new OctorateConnectionCreate("274", null, null, null), CancellationToken.None);

        using var bareBody = JsonDocument.Parse(bare.Requests[0].Body);
        Assert.False(bareBody.RootElement.TryGetProperty("hotelId", out _));
        Assert.False(bareBody.RootElement.TryGetProperty("username", out _));
        Assert.False(bareBody.RootElement.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task UpdateConnectionAsync_SendsOnlyTheMembersThatAreSet_AndDeleteConnectionAsync_CallsTheDeletePath()
    {
        var h = new OctorateConnectionClientHarness();
        using (h.Scope.Use(OrgA))
        {
            await h.Client.UpdateConnectionAsync(Acc, "7001", new OctorateConnectionPatch { UpdateCalendar = true, PullReservations = true }, CancellationToken.None);
            await h.Client.DeleteConnectionAsync(Acc, "7001", CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.UpdateConnectionAsync(Acc, "7001", new OctorateConnectionPatch(), CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.DeleteConnectionAsync(Acc, "not-a-number", CancellationToken.None));
        }

        Assert.Equal([HttpMethod.Patch, HttpMethod.Delete], h.Requests.Select(r => r.Method));
        Assert.All(h.Requests, r => Assert.Equal($"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection/{Acc}/7001", r.Uri.ToString()));
        using var body = JsonDocument.Parse(h.Requests[0].Body);
        Assert.Equal(["updateCalendar", "pullReservations"], body.RootElement.EnumerateObject().Select(p => p.Name));

        // A connection Octorate does not know is the typed 404.
        h.Answer(HttpStatusCode.NotFound, "{}");
        using (h.Scope.Use(OrgA))
            await Assert.ThrowsAsync<OctorateNotFoundException>(() => h.Client.DeleteConnectionAsync(Acc, "7001", CancellationToken.None));
    }

    [Theory]
    [InlineData(OctorateConnectionAction.ImportRooms, "IMPORT_ROOMS")]
    [InlineData(OctorateConnectionAction.ImportReservations, "IMPORT_RESERVATIONS")]
    [InlineData(OctorateConnectionAction.ImportCalendar, "IMPORT_CALENDAR")]
    [InlineData(OctorateConnectionAction.MarkConnected, "MARK_CONNECTED")]
    [InlineData(OctorateConnectionAction.CommitMappingAsCompleted, "COMMIT_MAPPING_AS_COMPLETED")]
    public async Task RunConnectionActionAsync_PostsTheActionOfTheOpenApi_AndReadsTheListedMappings(OctorateConnectionAction action, string name)
    {
        var h = new OctorateConnectionClientHarness();
        h.Answer(HttpStatusCode.OK, """[ { "portalConnection": 7001, "productId": 956528, "externalId": 3301, "id": 1 }, { "productId": 956528 } ]""");

        IReadOnlyList<OctorateRoomMapping> mappings;
        using (h.Scope.Use(OrgA))
            mappings = await h.Client.RunConnectionActionAsync(Acc, "7001", action, CancellationToken.None);

        var request = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection/{name}/{Acc}/7001", request.Uri.ToString());
        Assert.Equal([new OctorateRoomMapping("956528", "3301")], mappings);
    }

    [Fact]
    public async Task RunConnectionActionAsync_AnAnswerThatIsNotTheDocumentedList_IsNeverASuccess()
    {
        var h = new OctorateConnectionClientHarness();
        using (h.Scope.Use(OrgA))
        {
            // The wrapper of the specification names the action: a wrapper that names another one, a bare object and an error with HTTP 200 are unknown results.
            h.Answer(HttpStatusCode.OK, """{ "additionalInfo": { "actionPerformed": "MARK_CONNECTED" }, "data": [ { "productId": 1, "externalId": 2 } ] }""");
            var listed = await h.Client.RunConnectionActionAsync(Acc, "7001", OctorateConnectionAction.MarkConnected, CancellationToken.None);
            Assert.Equal([new OctorateRoomMapping("1", "2")], listed);

            foreach (var body in new[]
                     {
                         """{ "additionalInfo": { "actionPerformed": "IMPORT_ROOMS" }, "data": [] }""",
                         "{}",
                         """{ "type": "ApiError", "message": "failed" }""",
                         "<html>gateway</html>",
                     })
            {
                h.Answer(HttpStatusCode.OK, body);
                await Assert.ThrowsAsync<OctorateServerException>(
                    () => h.Client.RunConnectionActionAsync(Acc, "7001", OctorateConnectionAction.MarkConnected, CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task GetConnectionMappingAsync_JoinsTheRoomsOfTheChannelAndTheirMapping()
    {
        var h = new OctorateConnectionClientHarness();
        h.Answer(
            HttpStatusCode.OK,
            """
            [
              { "id": 3301, "roomName": "Double Room", "roomId": "DBL", "rateName": "Standard", "manageable": true },
              { "id": 3302, "roomName": "Suite", "manageable": false },
              { "id": 3303, "roomId": "TRP" },
              { "roomName": "no id" }
            ]
            """);
        h.Answer(HttpStatusCode.OK, """[ { "portalConnection": 7001, "productId": 956528, "externalId": 3301, "id": 1 } ]""");

        OctorateChannelRoom[] rooms;
        using (h.Scope.Use(OrgA))
            rooms = await h.Client.GetConnectionMappingAsync(Acc, "7001", CancellationToken.None);

        Assert.Equal(
            [
                $"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection/external/{Acc}/7001",
                $"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection/mapping/{Acc}/7001",
            ],
            h.Requests.Select(r => r.Uri.ToString()));
        Assert.All(h.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(
            [
                new OctorateChannelRoom("3301", "Double Room", "956528", "Standard", true),
                new OctorateChannelRoom("3302", "Suite", null, null, false),
                new OctorateChannelRoom("3303", "TRP", null, null, true),
            ],
            rooms);
    }

    [Fact]
    public async Task SaveConnectionMappingAsync_OneCallForEachMapping_AndEverythingIsCheckedBeforeTheFirstLeaves()
    {
        var h = new OctorateConnectionClientHarness();
        using (h.Scope.Use(OrgA))
        {
            await h.Client.SaveConnectionMappingAsync(
                Acc,
                "7001",
                [new OctorateRoomMapping("956528", "3301"), new OctorateRoomMapping("956529", "3302")],
                CancellationToken.None);

            Assert.Equal(
                [
                    $"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection/mapping/{Acc}/7001?product=956528&externalProduct=3301",
                    $"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/connection/mapping/{Acc}/7001?product=956529&externalProduct=3302",
                ],
                h.Requests.Select(r => r.Uri.ToString()));
            Assert.All(h.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));

            // An empty list is never «all»; a bad id in the second mapping stops the first from being sent.
            var sent = h.Requests.Count;
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.SaveConnectionMappingAsync(Acc, "7001", [], CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(
                () => h.Client.SaveConnectionMappingAsync(
                    Acc,
                    "7001",
                    [new OctorateRoomMapping("956528", "3301"), new OctorateRoomMapping(string.Empty, "3302")],
                    CancellationToken.None));
            Assert.Equal(sent, h.Requests.Count);
        }
    }

    [Theory]
    [InlineData(550, typeof(OctoratePartnerFailureException), 502)]
    [InlineData(500, typeof(OctorateServerException), 502)]
    [InlineData(404, typeof(OctorateNotFoundException), 404)]
    [InlineData(409, typeof(OctorateConflictException), 409)]
    [InlineData(403, typeof(OctorateAuthException), 502)]
    [InlineData(400, typeof(OctorateRequestRejectedException), 502)]
    public async Task EveryOperation_RaisesTheTypedErrorOfTheStatus_AndOnly550IsThePartnerFailure(int status, Type expected, int respondsWith)
    {
        var h = new OctorateConnectionClientHarness();
        using (h.Scope.Use(OrgA))
        {
            var calls = new Func<Task>[]
            {
                () => h.Client.ListConnectionsAsync(Acc, CancellationToken.None),
                () => h.Client.CreateConnectionAsync(Acc, new OctorateConnectionCreate("142", null, null, null), CancellationToken.None),
                () => h.Client.UpdateConnectionAsync(Acc, "7001", new OctorateConnectionPatch { Content = false }, CancellationToken.None),
                () => h.Client.DeleteConnectionAsync(Acc, "7001", CancellationToken.None),
                () => h.Client.RunConnectionActionAsync(Acc, "7001", OctorateConnectionAction.ImportRooms, CancellationToken.None),
                () => h.Client.GetConnectionMappingAsync(Acc, "7001", CancellationToken.None),
                () => h.Client.SaveConnectionMappingAsync(Acc, "7001", [new OctorateRoomMapping("1", "2")], CancellationToken.None),
            };
            foreach (var call in calls)
            {
                h.Answer((HttpStatusCode)status, """{ "message": "x" }""");
                var error = await Assert.ThrowsAnyAsync<OctorateException>(call);
                Assert.IsType(expected, error);
                Assert.Equal(respondsWith, error.ResponseStatusCode);
                Assert.Equal(status, error.OctorateStatusCode);
            }
        }

        // The messages of the partner failure exist in both languages and say something different.
        var partner = new OctoratePartnerFailureException();
        Assert.Equal(("octorate_partner_failed", "OctoratePartnerFailed", 550), (partner.Code, partner.MessageKey, partner.OctorateStatusCode));
    }

    [Fact]
    public async Task EveryOperation_NeedsAnOrganization_AndSendsNothingWithoutOne()
    {
        var h = new OctorateConnectionClientHarness();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.ListConnectionsAsync(Acc, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Client.RunConnectionActionAsync(Acc, "7001", OctorateConnectionAction.MarkConnected, CancellationToken.None));
        Assert.Empty(h.Requests);
    }
}
