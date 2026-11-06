using System.Net;
using System.Text;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The read of the calendar through the client (E02/F02/US01/T03): what it asks (the dates, <c>dateTo</c> included, the rooms, at most 20 a
/// call), and that an answer it cannot read is a failed read and never an empty calendar (a caller that deprecates something after a
/// successful read must not do it on a guess).
/// </summary>
public class OctorateCalendarClientTests
{
    private static readonly Guid Org = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a01");
    private static readonly DateOnly From = new(2026, 11, 6);
    private static readonly DateOnly To = new(2027, 11, 6);

    [Fact]
    public async Task TheCalendarOfARoom_IsAskedWithTheDatesAndTheRoom_AndTheDaysAreRead()
    {
        var h = new Harness();
        h.Serve("""{ "data": [ { "id": "956528", "days": [ { "date": "2026-11-10", "availability": 0, "price": 90, "stopSells": false } ] } ], "page": { "number": 0 } }""");

        OctorateCalendarDay[] days;
        using (h.Scope.Use(Org))
            days = await h.Client.GetCalendarAsync("585492", ["956528"], From, To, CancellationToken.None);

        var day = Assert.Single(days);
        Assert.Equal(new OctorateCalendarDay("956528", new DateOnly(2026, 11, 10), 0, 90m, false, null, null, null, null), day);

        // One call, for the org of the scope: the dates as days (dateTo is the last day, included) and the room.
        var request = Assert.Single(h.Requests);
        Assert.EndsWith("/rest/v1/calendar/585492?dateFrom=2026-11-06&dateTo=2027-11-06&products=956528", request.PathAndQuery);
        Assert.Equal(["Bearer token-" + Org.ToString("N")], h.Authorizations);
    }

    [Fact]
    public async Task MoreThanTwentyRooms_AreAskedInSeveralCalls_AndEachRoomOnce()
    {
        var h = new Harness();
        var rooms = Enumerable.Range(1, 45).Select(i => $"R{i}").ToList();
        h.Serve("""[ { "product": "R1", "date": "2026-11-10", "availability": 0, "price": 10 } ]""");
        h.Serve("""[ { "product": "R21", "date": "2026-11-10", "availability": 1, "price": 10 } ]""");
        h.Serve("""[ { "product": "R41", "date": "2026-11-10", "availability": 1, "price": 10 } ]""");

        OctorateCalendarDay[] days;
        using (h.Scope.Use(Org))
            days = await h.Client.GetCalendarAsync("585492", [.. rooms, "R1", "R2"], From, To, CancellationToken.None);

        Assert.Equal(3, h.Requests.Count);
        var asked = h.Requests.Select(r => Uri.UnescapeDataString(r.Query.Split("products=")[1]).Split(',')).ToList();
        Assert.Equal([20, 20, 5], asked.Select(a => a.Length));
        Assert.Equal(rooms, asked.SelectMany(a => a));
        Assert.Equal(["R1", "R21", "R41"], days.Select(d => d.ProductId));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": [], "page": { "number": 0 } }""")]
    [InlineData("""{ "message": "no content" }""")]
    public async Task AnAnswerWithNoDay_IsAFailedRead_NeverAnEmptyCalendar(string body)
    {
        var h = new Harness();
        h.Serve(body);

        using (h.Scope.Use(Org))
        {
            await Assert.ThrowsAsync<OctorateServerException>(
                () => h.Client.GetCalendarAsync("585492", ["956528"], From, To, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ACallThatCannotBeRight_IsRefusedBeforeAnythingLeaves()
    {
        var h = new Harness();

        using (h.Scope.Use(Org))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.GetCalendarAsync("585492", [], From, To, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.GetCalendarAsync("585492", ["956528"], To, From, CancellationToken.None));
        }

        Assert.Empty(h.Requests);
    }

    private sealed class Harness
    {
        public Harness()
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

        public QueueHandler Handler { get; } = new();

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }

        public IReadOnlyList<Uri> Requests => Handler.Requests;

        public IReadOnlyList<string> Authorizations => Handler.Authorizations;

        public void Serve(string body) => Handler.Bodies.Enqueue(body);
    }

    private sealed class FakeTokens : IOctorateTokenService
    {
        public Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct) => Task.FromResult("token-" + orgId.ToString("N"));

        public Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No token is refused in these tests.");

        public Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(Guid orgId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Answers each call with the next queued body and keeps the addresses and the tokens it was called with.</summary>
    private sealed class QueueHandler : HttpMessageHandler
    {
        public Queue<string> Bodies { get; } = new();

        public List<Uri> Requests { get; } = [];

        public List<string> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "(none)");
            var body = Bodies.Count > 0 ? Bodies.Dequeue() : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
