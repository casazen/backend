using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the reservation client and of the booking target (E08/F01/US01/T02): a create and a payment never carry a field of Octorate's
/// automatic charge and carry the booking's amounts to the cent (money rule, D9); a property whose mapping is not confirmed, is still to decide,
/// or belongs to another org has no target, so nothing is ever written to Octorate for it (data rule, OCT-04).
/// </summary>
public class OctorateReservationClientProofTests
{
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a31");
    private static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c32");

    /// <summary>
    /// PROOF TEST: money rule (D9). A reservation is created and a payment recorded through the client: the bodies Octorate receives hold no
    /// <c>scheduled*</c>, <c>preauth*</c>, <c>stopCron</c> or card member at any depth, the price of the stay and the payment are the booking's
    /// to the cent, and the create carries the configured channel, the booking code as <c>refer</c>, the room with its PMS room and
    /// <c>availability=true</c>.
    /// </summary>
    [Fact]
    public async Task OctorateReservationRequests_NoAutomaticCharge_AmountsExact_Test()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, """{ "id": 130219355, "refer": "K7Q2M9X4TB", "status": "CONFIRMED" }""");
        h.Answer(HttpStatusCode.OK, """{ "amount": 1234.56, "id": 21575084, "paymentMode": "BANKTRANSFER" }""");
        var target = new OctorateBookingTarget("585492", "956528", "956527");
        var create = new OctorateReservationCreate
        {
            ExternalReference = "K7Q2M9X4TB",
            CheckIn = new DateOnly(2026, 12, 10),
            CheckOut = new DateOnly(2026, 12, 13),
            Guests = 3,
            Children = 1,
            RoomGross = 1234.56m,
            BookerFirstName = "Giulia",
            BookerLastName = "Rossi",
            BookerGender = Gender.Female,
        };

        OctorateReservation created;
        OctoratePaymentResult paid;
        using (h.Scope.Use(OrgA))
        {
            created = await h.Client.CreateReservationAsync(target, create, CancellationToken.None);
            paid = await h.Client.RegisterPaymentAsync(
                "585492",
                created.Id,
                new OctoratePaymentRegistration("pi_3QxYz", "BANKTRANSFER", 1234.56m, new DateTime(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc)),
                CancellationToken.None);
        }

        Assert.Equal("130219355", created.Id);
        Assert.Equal("21575084", paid.PaymentId);
        Assert.Equal(2, h.Requests.Count);

        // The create: the configured channel, the booking code, the room and its PMS room, the Rome days as UTC instants, the price to the cent.
        var createRequest = h.Requests[0];
        Assert.Equal(HttpMethod.Post, createRequest.Method);
        Assert.EndsWith("/rest/v1/reservation/585492?availability=true", createRequest.Uri.PathAndQuery);
        using (var document = JsonDocument.Parse(createRequest.Body))
        {
            var body = document.RootElement;
            Assert.Equal(270, body.GetProperty("channelId").GetInt32());
            Assert.Equal("K7Q2M9X4TB", body.GetProperty("refer").GetString());
            Assert.Equal("CONFIRMED", body.GetProperty("status").GetString());
            Assert.Equal(956528, body.GetProperty("product").GetInt64());
            Assert.Equal(956527, body.GetProperty("pmsProduct").GetInt64());
            Assert.Equal("2026-12-09T23:00:00+00:00[UTC]", body.GetProperty("checkin").GetString());
            Assert.Equal("2026-12-12T23:00:00+00:00[UTC]", body.GetProperty("checkout").GetString());
            Assert.Equal(1234.56m, body.GetProperty("roomGross").GetDecimal());
            Assert.Equal(3, body.GetProperty("totalGuest").GetInt32());
            Assert.Equal(1, body.GetProperty("totalChildren").GetInt32());

            // The booker by name only: no e-mail, no phone, no document reaches the channel manager.
            var booker = Assert.Single(body.GetProperty("guests").EnumerateArray());
            Assert.Equal("BOOKER", booker.GetProperty("type").GetString());
            Assert.Equal("FEMALE", booker.GetProperty("sex").GetString());
            Assert.False(booker.TryGetProperty("email", out _));
            Assert.False(booker.TryGetProperty("phone", out _));
        }

        // The payment: the transaction, the mode and the amount to the cent, as income.
        var paymentRequest = h.Requests[1];
        Assert.Equal(HttpMethod.Post, paymentRequest.Method);
        Assert.EndsWith("/rest/v1/reservation/585492/130219355/payment", paymentRequest.Uri.PathAndQuery);
        using (var document = JsonDocument.Parse(paymentRequest.Body))
        {
            var body = document.RootElement;
            Assert.Equal("pi_3QxYz", body.GetProperty("transaction").GetString());
            Assert.Equal("BANKTRANSFER", body.GetProperty("paymentMode").GetString());
            Assert.Equal(1234.56m, body.GetProperty("amount").GetDecimal());
            Assert.Equal("2026-11-06T09:30:00Z", body.GetProperty("referenceTime").GetString());
        }

        // No member of Octorate's automatic charge, at any depth of either body (D9).
        foreach (var request in h.Requests)
        {
            using var document = JsonDocument.Parse(request.Body);
            var charging = MemberNames(document.RootElement)
                .Where(name => name.StartsWith("scheduled", StringComparison.OrdinalIgnoreCase)
                               || name.StartsWith("preauth", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("stopCron", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("creditCard", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("paymentStep", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(charging);
        }
    }

    /// <summary>
    /// PROOF TEST: data rule (OCT-04). Only a property with a confirmed mapping, of a connected org, outside every row still to decide, and
    /// visible to the caller has a target: a proposed mapping, a candidate of an ambiguous row, a confirmed property that is still a candidate
    /// elsewhere, a property of another org and an org whose connection is not connected all answer "not mirrored", and Octorate is not even
    /// called for them.
    /// </summary>
    [Fact]
    public async Task OctorateBookingTargets_UnconfirmedOrBlockedMapping_HasNoTarget_Test()
    {
        var database = $"octorate-targets-{Guid.NewGuid():N}";
        Guid proposed, candidate, blocked, confirmed, foreign;
        await using (var seed = NewDb(database, null))
        {
            var connectionA = SeedConnection(seed, OrgA, OctorateConnectionStatus.Connected);
            var connectionB = SeedConnection(seed, OrgB, OctorateConnectionStatus.Connected);
            proposed = SeedProperty(seed, OrgA, "Casa Proposta");
            candidate = SeedProperty(seed, OrgA, "Casa Candidata");
            blocked = SeedProperty(seed, OrgA, "Casa Confermata ma Candidata");
            confirmed = SeedProperty(seed, OrgA, "Casa Confermata");
            foreign = SeedProperty(seed, OrgB, "Casa di un'altra organizzazione");
            seed.OctorateListingMappings.AddRange(
                NewMapping(OrgA, connectionA, "1001", OctorateMappingStatus.Proposed, proposed),
                NewMapping(OrgA, connectionA, "1002", OctorateMappingStatus.Ambiguous, null, $"[\"{candidate}\",\"{blocked}\"]"),
                NewMapping(OrgA, connectionA, "1003", OctorateMappingStatus.Confirmed, blocked),
                NewMapping(OrgA, connectionA, "1004", OctorateMappingStatus.Confirmed, confirmed),
                NewMapping(OrgB, connectionB, "2001", OctorateMappingStatus.Confirmed, foreign));
            await seed.SaveChangesAsync();
        }

        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetPmsRoomsAsync("1004", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new OctoratePmsRoom("956527", "956528", "Sky 001 Real Room"), new OctoratePmsRoom("777001", "777000", "Altra") });

        await using var asOrgA = NewDb(database, new RequestTenant(OrgA));
        var targets = Targets(asOrgA, client.Object, new RequestTenant(OrgA));

        foreach (var property in new[] { proposed, candidate, blocked, foreign, Guid.NewGuid() })
        {
            var lookup = await targets.LookupAsync(property, CancellationToken.None);
            Assert.Null(lookup.Target);
            Assert.Equal(OctorateTargetGap.NotMirrored, lookup.Gap);
        }

        // The control: the confirmed property of the connected org books into its room, with the PMS room of that unit only.
        var target = await targets.LookupAsync(confirmed, CancellationToken.None);
        Assert.Equal(OctorateTargetGap.None, target.Gap);
        Assert.Equal(new OctorateBookingTarget("1004", "956528", "956527"), target.Target);

        // An org whose connection is not connected has no target either, even with a confirmed mapping.
        await using (var seed = NewDb(database, null))
        {
            var connection = await seed.OctorateConnections.SingleAsync(c => c.OrgId == OrgA);
            connection.Status = OctorateConnectionStatus.Disconnected;
            await seed.SaveChangesAsync();
        }

        await using (var afterDisconnect = NewDb(database, new RequestTenant(OrgA)))
        {
            var lookup = await Targets(afterDisconnect, client.Object, new RequestTenant(OrgA)).LookupAsync(confirmed, CancellationToken.None);
            Assert.Equal(OctorateTargetGap.NotMirrored, lookup.Gap);
        }

        // Octorate was asked only for the property that has a target.
        client.Verify(c => c.GetPmsRoomsAsync("1004", It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();
    }

    /// <summary>A confirmed mapping whose accommodation has two rooms gets no room by guess, and Octorate is not asked again for five minutes.</summary>
    [Fact]
    public async Task ConfirmedMappingWithSeveralRooms_IsRoomNotChosen_AndIsNotAskedAgainAtOnce()
    {
        var database = $"octorate-targets-rooms-{Guid.NewGuid():N}";
        Guid property;
        await using (var seed = NewDb(database, null))
        {
            var connection = SeedConnection(seed, OrgA, OctorateConnectionStatus.Connected);
            property = SeedProperty(seed, OrgA, "Casa con due camere");
            var mapping = NewMapping(OrgA, connection, "1005", OctorateMappingStatus.Confirmed, property);
            mapping.OctorateProductId = null;
            seed.OctorateListingMappings.Add(mapping);
            await seed.SaveChangesAsync();
        }

        var client = new Mock<IOctorateApiClient>(MockBehavior.Strict);
        client
            .Setup(c => c.GetRoomsAsync("1005", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new OctorateRoom { Id = "1" }, new OctorateRoom { Id = "2" } });
        var cache = new MemoryCache(new MemoryCacheOptions());

        await using var db = NewDb(database, new RequestTenant(OrgA));
        var targets = Targets(db, client.Object, new RequestTenant(OrgA), cache);

        Assert.Equal(OctorateTargetGap.RoomNotChosen, (await targets.LookupAsync(property, CancellationToken.None)).Gap);
        Assert.Equal(OctorateTargetGap.RoomNotChosen, (await targets.LookupAsync(property, CancellationToken.None)).Gap);

        client.Verify(c => c.GetRoomsAsync("1005", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null((await db.OctorateListingMappings.AsNoTracking().SingleAsync()).OctorateProductId);
    }

    private static IEnumerable<string> MemberNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    yield return member.Name;
                    foreach (var inner in MemberNames(member.Value))
                        yield return inner;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in MemberNames(item))
                        yield return inner;
                }

                break;
        }
    }

    private static OctorateBookingTargets Targets(
        AppDbContext db,
        IOctorateApiClient client,
        ITenantContext tenant,
        IMemoryCache? cache = null)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var mapping = new ChannelMappingService(
            db,
            client,
            new OctorateOrgScope(tenant),
            new OctorateRateLimiter(TimeProvider.System),
            Mock.Of<IHostScopeResolver>(),
            Mock.Of<IPropertyService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IInternalActivityService>(),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions()),
            host.Object,
            TimeProvider.System,
            NullLogger<ChannelMappingService>.Instance);
        return new OctorateBookingTargets(
            db,
            client,
            new OctorateOrgScope(tenant),
            mapping,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            host.Object,
            TimeProvider.System,
            NullLogger<OctorateBookingTargets>.Instance);
    }

    private static AppDbContext NewDb(string database, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options, tenant ?? NullTenantContext.Instance);

    private static Guid SeedConnection(AppDbContext db, Guid orgId, OctorateConnectionStatus status)
    {
        var connection = new OctorateConnection { OrgId = orgId, Environment = OctorateEnvironment.Test, Status = status };
        db.OctorateConnections.Add(connection);
        return connection.Id;
    }

    private static Guid SeedProperty(AppDbContext db, Guid orgId, string name)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = name,
            Address = "Via Prova 1",
            City = "Como",
            MaxGuests = 4,
        };
        db.Properties.Add(property);
        return property.Id;
    }

    private static OctorateListingMapping NewMapping(
        Guid orgId,
        Guid connectionId,
        string accommodationId,
        OctorateMappingStatus status,
        Guid? propertyId,
        string candidatesJson = OctorateListingMapping.EmptyJsonArray) => new()
        {
            OrgId = orgId,
            ConnectionId = connectionId,
            OctorateAccommodationId = accommodationId,
            OctorateProductId = "956528",
            SnapshotName = "Listing " + accommodationId,
            Status = status,
            PropertyId = propertyId,
            CandidatePropertyIdsJson = candidatesJson,
        };

    /// <summary>The client over a handler that answers what the test queued and keeps every request with its body.</summary>
    private sealed class ClientHarness
    {
        public ClientHarness()
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect",
                    ["Octorate:DirectChannelId"] = "270",
                })
                .Build();
            Scope = new OctorateOrgScope(NullTenantContext.Instance);
            Client = new OctorateApiClient(
                new HttpClient(Handler),
                new FakeTokens(),
                new OctorateRateLimiter(clock),
                Scope,
                configuration,
                clock,
                NullLogger<OctorateApiClient>.Instance);
        }

        public RecordingHandler Handler { get; } = new();

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }

        public IReadOnlyList<RecordedRequest> Requests => Handler.Requests;

        public void Answer(HttpStatusCode status, string body) => Handler.Answers.Enqueue((status, body));
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Answers { get; } = new();

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            var (status, answer) = Answers.Count > 0 ? Answers.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeTokens : IOctorateTokenService
    {
        public Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct) => Task.FromResult("token-" + orgId.ToString("N"));

        public Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No token is refused in these tests.");

        public Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(Guid orgId, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetPartnerAccessTokenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");

        public Task<string> RefreshRejectedPartnerTokenAsync(string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No partner-level call is made in these tests.");
    }
}
