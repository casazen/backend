using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The client call that cancels a reservation (E08/F02/US02/T02, NF-76): the reservation is read; a reservation that is cancelled already sends
/// nothing; otherwise the same document goes back with <c>PUT</c> and only its status changed; Octorate refusing that way of cancelling it makes
/// the client <c>DELETE</c> the reservation; every other answer is the typed error and never leads to a delete.
/// </summary>
public class OctorateCancelReservationClientTests
{
    private static readonly OctorateBookingTarget Target = new("585492", "956528", "956527");
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a91");

    private const string ConfirmedReservation =
        """{ "id": 7001, "refer": "K7Q2M9X4TB", "channelId": 274, "status": "CONFIRMED", "product": 956528, "pmsProduct": 956527, "roomGross": 300.5, "updateTime": "2026-11-05T10:00:00+00:00[UTC]", "guests": [ { "type": "BOOKER", "givenName": "Giulia", "familyName": "Rossi" } ] }""";

    [Fact]
    public async Task CancelReservationAsync_AReservationCancelledAlready_SendsNothing()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, """{ "id": 7001, "status": "CANCELLED" }""");

        using (h.Scope.Use(OrgA))
            await h.Client.CancelReservationAsync(Target, "7001", CancellationToken.None);

        var read = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.EndsWith("/rest/v1/reservation/585492/7001", read.Uri.PathAndQuery);
    }

    [Fact]
    public async Task CancelReservationAsync_PutAccepted_SendsTheWholeReservationWithOnlyTheStatusChanged()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, ConfirmedReservation);
        h.Answer(HttpStatusCode.OK, ConfirmedReservation.Replace("CONFIRMED", "CANCELLED", StringComparison.Ordinal));

        using (h.Scope.Use(OrgA))
            await h.Client.CancelReservationAsync(Target, "7001", CancellationToken.None);

        Assert.Equal([HttpMethod.Get, HttpMethod.Put], h.Requests.Select(r => r.Method).ToList());
        var put = h.Requests[1];
        Assert.EndsWith("/rest/v1/reservation/585492/7001", put.Uri.PathAndQuery);
        using var document = JsonDocument.Parse(put.Body);
        var body = document.RootElement;
        Assert.Equal("CANCELLED", body.GetProperty("status").GetString());

        // Nothing else of the reservation is rewritten: the dates, the price, the guests stay what Octorate gave.
        Assert.Equal("K7Q2M9X4TB", body.GetProperty("refer").GetString());
        Assert.Equal(274, body.GetProperty("channelId").GetInt32());
        Assert.Equal(956528, body.GetProperty("product").GetInt64());
        Assert.Equal(956527, body.GetProperty("pmsProduct").GetInt64());
        Assert.Equal(300.5m, body.GetProperty("roomGross").GetDecimal());
        Assert.Equal("2026-11-05T10:00:00+00:00[UTC]", body.GetProperty("updateTime").GetString());
        var booker = Assert.Single(body.GetProperty("guests").EnumerateArray());
        Assert.Equal("Giulia", booker.GetProperty("givenName").GetString());
    }

    [Fact]
    public async Task CancelReservationAsync_OctorateSaysItCannotBeCancelledThatWay_DeletesTheReservation()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, ConfirmedReservation);
        h.Answer(HttpStatusCode.BadRequest, """{ "type": "ApiValidationFailed" }""");
        h.Answer(HttpStatusCode.OK, "{}");

        using (h.Scope.Use(OrgA))
            await h.Client.CancelReservationAsync(Target, "7001", CancellationToken.None);

        Assert.Equal([HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete], h.Requests.Select(r => r.Method).ToList());
        Assert.EndsWith("/rest/v1/reservation/585492/7001", h.Requests[2].Uri.PathAndQuery);
    }

    [Fact]
    public async Task CancelReservationAsync_AnyOtherAnswer_IsTheTypedError_AndNeverDeletes()
    {
        foreach (var (status, expected) in new (HttpStatusCode, Type)[]
                 {
                     (HttpStatusCode.InternalServerError, typeof(OctorateServerException)),
                     (HttpStatusCode.Conflict, typeof(OctorateConflictException)),
                     (HttpStatusCode.NotFound, typeof(OctorateNotFoundException)),
                     (HttpStatusCode.Forbidden, typeof(OctorateAuthException)),
                 })
        {
            var h = new ClientHarness();
            h.Answer(HttpStatusCode.OK, ConfirmedReservation);
            h.Answer(status, "{}");

            using (h.Scope.Use(OrgA))
            {
                var error = await Assert.ThrowsAnyAsync<OctorateException>(() => h.Client.CancelReservationAsync(Target, "7001", CancellationToken.None));
                Assert.IsType(expected, error);
            }

            // The read and the PUT: a delete is for the answer that says the reservation cannot be cancelled that way, and for no other.
            Assert.Equal([HttpMethod.Get, HttpMethod.Put], h.Requests.Select(r => r.Method).ToList());
        }
    }

    [Fact]
    public void CancelBody_CompletesAReservationThatLeftOutItsRoom_AndRefusesWhatIsNotAnObject()
    {
        using var bare = JsonDocument.Parse("""{ "id": 7001, "status": "CONFIRMED" }""");
        using var complete = JsonDocument.Parse(ConfirmedReservation);
        using var list = JsonDocument.Parse("[]");

        using (var completed = JsonDocument.Parse(OctorateReservationRequests.CancelBody(bare.RootElement, Target)))
        {
            Assert.Equal("CANCELLED", completed.RootElement.GetProperty("status").GetString());
            Assert.Equal(956528, completed.RootElement.GetProperty("product").GetInt64());
            Assert.Equal(956527, completed.RootElement.GetProperty("pmsProduct").GetInt64());
        }

        // A document that has its own room keeps it: the room of the target does not replace what Octorate says.
        using (var kept = JsonDocument.Parse(OctorateReservationRequests.CancelBody(complete.RootElement, new OctorateBookingTarget("585492", "111", "222"))))
        {
            Assert.Equal(956528, kept.RootElement.GetProperty("product").GetInt64());
            Assert.Equal(956527, kept.RootElement.GetProperty("pmsProduct").GetInt64());
        }

        Assert.Throws<ArgumentException>(() => OctorateReservationRequests.CancelBody(list.RootElement, Target));
    }

    private sealed class ClientHarness
    {
        public ClientHarness()
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" })
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
