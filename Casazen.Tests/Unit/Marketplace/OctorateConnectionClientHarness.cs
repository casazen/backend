using System.Net;
using System.Text;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// The real <see cref="OctorateApiClient"/> over a stub HTTP handler, for the tests of the operations on a connection (E09/F02/US01/T01): the handler
/// keeps every request (method, address, body) and answers what the test queued, so what is proven is what the client sends, reads and logs, not
/// Octorate.
/// </summary>
internal sealed class OctorateConnectionClientHarness
{
    public const string BaseAddress = "https://api.octorate.test/connect";

    public OctorateConnectionClientHarness()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = BaseAddress })
            .Build();
        Scope = new OctorateOrgScope(NullTenantContext.Instance);
        Client = new OctorateApiClient(
            new HttpClient(Handler),
            new FakeTokens(),
            new OctorateRateLimiter(clock),
            Scope,
            configuration,
            clock,
            Logs);
    }

    public RecordingHandler Handler { get; } = new();

    public CapturingLogger<OctorateApiClient> Logs { get; } = new();

    public OctorateOrgScope Scope { get; }

    public OctorateApiClient Client { get; }

    public IReadOnlyList<RecordedRequest> Requests => Handler.Requests;

    /// <summary>Queues the answer of the next call.</summary>
    public void Answer(HttpStatusCode status, string body) => Handler.Answers.Enqueue(() => new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    });

    /// <summary>Queues a failure of the next call (no answer at all).</summary>
    public void Fail(Exception exception) => Handler.Answers.Enqueue(() => throw exception);

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    public sealed class RecordingHandler : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Answers { get; } = new();

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            return Answers.Count > 0
                ? Answers.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
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
