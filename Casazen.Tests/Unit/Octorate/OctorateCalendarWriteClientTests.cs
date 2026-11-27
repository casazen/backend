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
/// E08/F05/US02/T01, T02 (NF-36, D7): the bulk write of the calendar and the read of the process it starts, through the client. What the body says (the names of the OpenAPI,
/// nothing that is not set), and that an answer is read for what it is: a process id is the only acceptance, a 200 without one wrote nothing, <c>success: false</c> is a refusal
/// and an answer that cannot be read is a failure, never a success.
/// </summary>
public class OctorateCalendarWriteClientTests
{
    private static readonly Guid Org = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a01");
    private static readonly DateOnly Day = new(2026, 12, 4);

    [Fact]
    public async Task TheBulkCall_IsOneArrayOfIntervals_WithTheNamesOfTheOpenApi_AndOnlyTheValuesThatAreSet()
    {
        var h = new Harness();
        h.Serve("""{ "success": true, "process": [ 12345678, 12345679 ] }""");
        var updates = new[]
        {
            new OctorateCalendarUpdate(
                "956528",
                Day,
                Day.AddDays(2),
                new OctorateCalendarValues(null, 114.99m, 2, null, false, true, false, 3)),
            new OctorateCalendarUpdate("956528", Day.AddDays(3), Day.AddDays(3), new OctorateCalendarValues(0, 100m, 1, 999, null, false, false, 0)),
        };

        OctorateCalendarUpdateResult result;
        using (h.Scope.Use(Org))
            result = await h.Client.UpdateCalendarAsync(updates, CancellationToken.None);

        Assert.Equal(["12345678", "12345679"], result.ProcessIds);
        var request = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/rest/v1/calendar/bulk", request.Uri.AbsolutePath);
        Assert.Equal("Bearer token-" + Org.ToString("N"), request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        var items = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(956528, items[0].GetProperty("room").GetInt64());
        Assert.Equal("2026-12-04", items[0].GetProperty("dateFrom").GetString());
        Assert.Equal("2026-12-06", items[0].GetProperty("dateTo").GetString());
        var first = items[0].GetProperty("values");
        Assert.Equal(114.99m, first.GetProperty("price").GetDecimal());
        Assert.Equal(2, first.GetProperty("minstay").GetInt32());
        Assert.False(first.GetProperty("stopSells").GetBoolean());
        Assert.True(first.GetProperty("closeToArrival").GetBoolean());
        Assert.False(first.GetProperty("closeToDeparture").GetBoolean());
        Assert.Equal(3, first.GetProperty("cutOffDays").GetInt32());
        // What is not set is not sent: Octorate keeps what it holds.
        Assert.False(first.TryGetProperty("availability", out _));
        Assert.False(first.TryGetProperty("maxstay", out _));
        var second = items[1].GetProperty("values");
        Assert.Equal(0, second.GetProperty("availability").GetInt32());
        Assert.Equal(999, second.GetProperty("maxstay").GetInt32());
        Assert.False(second.TryGetProperty("stopSells", out _));
    }

    [Fact]
    public async Task AnAnswerWithNoProcessId_IsAResultWithNoId_NeverAnAcceptance()
    {
        var h = new Harness();
        h.Serve("""{ "success": true, "process": [], "warnings": [ "License for Room 956528 has expired! This room has been ignored" ] }""");
        h.Serve("""{ "success": true }""");

        using (h.Scope.Use(Org))
        {
            var emptied = await h.Client.UpdateCalendarAsync([Update()], CancellationToken.None);
            var missing = await h.Client.UpdateCalendarAsync([Update()], CancellationToken.None);

            Assert.Empty(emptied.ProcessIds);
            Assert.Empty(missing.ProcessIds);
        }
    }

    /// <summary>
    /// Any warning of the channel manager is a chunk that was not taken whole (octorate-integration-facts.md): ids together with a non-empty <c>warnings</c> are a refusal of the
    /// request, never an acceptance; an empty list of warnings changes nothing.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithProcessIdsAndAWarning_IsARefusalOfTheRequest_NeverAnAcceptance()
    {
        var h = new Harness();
        h.Serve("""{ "success": true, "process": [ 12345678 ], "warnings": [ "A date of the interval was ignored" ] }""");
        h.Serve("""{ "success": true, "process": [ 12345678 ], "warnings": "A date of the interval was ignored" }""");
        h.Serve("""{ "success": true, "process": [ 12345678 ], "warnings": [] }""");

        using (h.Scope.Use(Org))
        {
            await Assert.ThrowsAsync<OctorateRequestRejectedException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));
            await Assert.ThrowsAsync<OctorateRequestRejectedException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));

            var accepted = await h.Client.UpdateCalendarAsync([Update()], CancellationToken.None);
            Assert.Equal(["12345678"], accepted.ProcessIds);
        }
    }

    [Fact]
    public async Task SuccessFalse_IsARefusalOfTheRequest_AndAnAnswerThatCannotBeRead_IsAFailureNeverASuccess()
    {
        var h = new Harness();
        h.Serve("""{ "error": "Room not found at index 0!", "success": false }""");
        h.Serve("not json at all");
        h.Serve("""{ "process": [ 1 ] }""");
        h.Serve("[]");

        using (h.Scope.Use(Org))
        {
            await Assert.ThrowsAsync<OctorateRequestRejectedException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));
            // No «success: true»: the ids are not an acceptance.
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));
        }
    }

    [Fact]
    public async Task ACallThatCannotBeRight_IsRefusedBeforeAnythingLeaves()
    {
        var h = new Harness();

        using (h.Scope.Use(Org))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.UpdateCalendarAsync([], CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(
                () => h.Client.UpdateCalendarAsync([new OctorateCalendarUpdate("not-a-number", Day, Day, new OctorateCalendarValues(Price: 10m))], CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(
                () => h.Client.UpdateCalendarAsync([new OctorateCalendarUpdate("956528", Day.AddDays(1), Day, new OctorateCalendarValues(Price: 10m))], CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(
                () => h.Client.UpdateCalendarAsync([new OctorateCalendarUpdate("956528", Day, Day, new OctorateCalendarValues())], CancellationToken.None));
        }

        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task TheProcessOfACall_IsReadWithItsStatusAndItsDates()
    {
        var h = new Harness();
        h.Serve("""
            { "id": 12345678, "status": "NEW", "room": 956528, "dateFrom": "2026-12-04", "dateTo": "2026-12-05",
              "sent": [ { "date": "2026-12-04", "status": "NOT_PROCESSED", "siteId": 1 }, { "date": "2026-12-05", "status": "ERROR", "siteId": 1 } ] }
            """);

        OctorateCalendarProcess process;
        using (h.Scope.Use(Org))
            process = await h.Client.GetCalendarProcessAsync("12345678", CancellationToken.None);

        Assert.Equal("12345678", process.Id);
        Assert.Equal("NEW", process.Status);
        Assert.Equal(
            [(new DateOnly(2026, 12, 4), "NOT_PROCESSED"), (new DateOnly(2026, 12, 5), "ERROR")],
            process.Dates.Select(d => (d.Date!.Value, d.Status!)));
        Assert.EndsWith("/rest/v1/calendar/process/12345678", Assert.Single(h.Requests).Uri.AbsolutePath);
        Assert.Equal(CalendarProcessVerdict.Error, CalendarPushRules.VerdictOf(process, new HashSet<string>()));
    }

    [Fact]
    public async Task AProcessThatReportsAnErrorOfItsOwn_OrCannotBeRead_IsAFailureOfTheRead_NeverAState()
    {
        var h = new Harness();
        h.Serve("""{ "error": "Error loading process info!", "id": 123456, "status": "NEW" }""");
        h.Serve("nope");

        using (h.Scope.Use(Org))
        {
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetCalendarProcessAsync("123456", CancellationToken.None));
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetCalendarProcessAsync("123456", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.GetCalendarProcessAsync("  ", CancellationToken.None));
        }
    }

    [Fact]
    public async Task AProcessOctorateDoesNotKnow_IsNotFound_TheEndpointBeingDocumentedOnlyInPostman()
    {
        var h = new Harness();
        h.Serve("""{ "type": "ApiResourceMissing" }""", HttpStatusCode.NotFound);

        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<OctorateNotFoundException>(() => h.Client.GetCalendarProcessAsync("123456", CancellationToken.None));
    }

    [Fact]
    public async Task AQuotaThatIsUsedUp_IsAWaitOfItsOwn_NotARefusalOfTheAccess_OnTheWriteAndOnTheRead()
    {
        var h = new Harness();
        // Octorate answers a quota that is used up with a 403 of its own type (OCT-13): the status of a refusal of the access, but not one.
        h.Serve("""{ "type": "ApiQuotaException" }""", HttpStatusCode.Forbidden);
        h.Serve("""{ "type": "ApiQuotaException" }""", HttpStatusCode.Forbidden);
        h.Serve("""{ "type": "ApiSecurityException" }""", HttpStatusCode.Forbidden);

        using (h.Scope.Use(Org))
        {
            var write = await Assert.ThrowsAsync<OctorateRateLimitException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));
            var read = await Assert.ThrowsAsync<OctorateRateLimitException>(() => h.Client.GetCalendarProcessAsync("123456", CancellationToken.None));

            Assert.True(write.QuotaExhausted);
            Assert.True(read.QuotaExhausted);
            Assert.Equal("octorate_rate_limited", write.Code);

            // A 403 that is a refusal of the access keeps its meaning.
            await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.UpdateCalendarAsync([Update()], CancellationToken.None));
        }
    }

    private static OctorateCalendarUpdate Update() => new("956528", Day, Day, new OctorateCalendarValues(Price: 100m, StopSells: false));

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

        public IReadOnlyList<Seen> Requests => Handler.Requests;

        public void Serve(string body, HttpStatusCode status = HttpStatusCode.OK) => Handler.Answers.Enqueue((status, body));
    }

    private sealed record Seen(HttpMethod Method, Uri Uri, string Authorization, string Body);

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

    /// <summary>Answers each call with the next queued status and body and keeps what it was called with.</summary>
    private sealed class QueueHandler : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Answers { get; } = new();

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString() ?? "(none)", body));
            var (status, answer) = Answers.Count > 0 ? Answers.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
