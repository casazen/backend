using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The three operations of the reviews of the client of the channel manager (E05/F06/US01/T02, OCT-12, OCT-13): the read of the reviews of a booking, the host's reply and the host's own
/// review. What is proven is the request that leaves (the address, the method, the body) and what a refusal becomes: a 403 that is the page of the application server is «the reviews are
/// not available» and a typed 403 keeps its common meaning; a 409 to a write is «not allowed or done already»; a failure of the channel manager on a write is never repeated by the client;
/// an answer nobody can read is never an empty review. The member names of the answers are documented, not proven (the sandbox has no reviews).
/// </summary>
public class OtaReviewClientTests
{
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a93");

    private const string ServerPage = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"><html><head><title>Application server - Error report</title></head><body>403</body></html>";

    [Fact]
    public async Task GetReview_ReadsTheReviewsOfOneBooking_FromTheReviewPath()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, """[ { "type": "GUEST_TO_HOST", "status": "REVIEW_PUBLISHED", "overallRating": 5, "publicReview": "Splendido" } ]""");

        OctorateReview review;
        using (h.Scope.Use(OrgA))
            review = await h.Client.GetReviewAsync("585001", "HMGRANTED1", CancellationToken.None);

        var request = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/rest/v2/review/585001/HMGRANTED1", request.Uri.PathAndQuery, StringComparison.Ordinal);
        Assert.Equal("Splendido", review.Guest?.Text);
        Assert.Equal(5m, review.Guest?.Rating);
    }

    [Fact]
    public async Task GetReview_AnAnswerThatSaysThereIsNoReview_IsEmpty_AndOneNobodyCanReadIsAServerError()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, "[]");
        h.Answer(HttpStatusCode.OK, "\"nothing\"");

        using (h.Scope.Use(OrgA))
        {
            var none = await h.Client.GetReviewAsync("585001", "HMGRANTED1", CancellationToken.None);
            Assert.True(none.IsEmpty);

            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetReviewAsync("585001", "HMGRANTED1", CancellationToken.None));
        }
    }

    /// <summary>
    /// PROOF TEST: trust rule (ADR-023-R6). A 403 that is not an error of the API of the channel manager (the page of its application server, which a call without the permission gets)
    /// is «the reviews are not available» (a state of the read), decided by the status and the shape of the answer, never by its text; a typed 403 keeps its common meaning.
    /// </summary>
    [Fact]
    public async Task OctorateClient_ReviewRead_403WithTheServerPage_IsReviewNotAvailable_Test()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.Forbidden, ServerPage);
        h.Answer(HttpStatusCode.Forbidden, """{"type":"ApiSecurityException","message":"No","suggestion":"errRefreshTokenInvalid"}""");
        h.Answer(HttpStatusCode.Forbidden, """{"type":"WrongEnviroment"}""");

        using (h.Scope.Use(OrgA))
        {
            var refused = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.GetReviewAsync("585001", "HMGRANTED1", CancellationToken.None));
            Assert.True(refused.IsReviewNotAvailable);
            Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, refused.Code);
            Assert.Equal(403, refused.OctorateStatusCode);
            Assert.Equal(409, refused.ResponseStatusCode);

            var typed = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.GetReviewAsync("585001", "HMGRANTED1", CancellationToken.None));
            Assert.False(typed.IsReviewNotAvailable);
            Assert.Equal("octorate_auth_failed", typed.Code);

            await Assert.ThrowsAsync<OctorateWrongEnvironmentException>(() => h.Client.GetReviewAsync("585001", "HMGRANTED1", CancellationToken.None));
        }
    }

    [Fact]
    public async Task SendHostReply_PostsTheTextToTheReplyPath_OnceAndNeverRepeatedAfterAFailure()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, "{}");
        h.Answer(HttpStatusCode.InternalServerError, "{}");

        using (h.Scope.Use(OrgA))
        {
            await h.Client.SendHostReplyAsync("585001", "HMGRANTED1", "Grazie di cuore!", CancellationToken.None);
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.SendHostReplyAsync("585001", "HMGRANTED1", "Grazie ancora!", CancellationToken.None));
        }

        // One request for each call: a failure of the channel manager on a write is the caller's to decide on (OCT-13), never a second request of the client.
        Assert.Equal(2, h.Requests.Count);
        Assert.All(h.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.All(h.Requests, r => Assert.EndsWith("/rest/v2/review/585001/HMGRANTED1/sendHostReply", r.Uri.PathAndQuery, StringComparison.Ordinal));
        using var body = JsonDocument.Parse(h.Requests[0].Body);
        var member = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("text", member.Name);
        Assert.Equal("Grazie di cuore!", member.Value.GetString());
    }

    [Fact]
    public async Task SendHostReply_A409_IsNotAllowedOrDoneAlready_AndA403PageIsReviewNotAvailable()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.Conflict, "{}");
        h.Answer(HttpStatusCode.Forbidden, ServerPage);

        using (h.Scope.Use(OrgA))
        {
            var conflict = await Assert.ThrowsAsync<OctorateReviewActionNotAllowedException>(
                () => h.Client.SendHostReplyAsync("585001", "HMGRANTED1", "Grazie!", CancellationToken.None));
            Assert.Equal(OctorateReviewActionNotAllowedException.CodeValue, conflict.Code);
            Assert.Equal(409, conflict.ResponseStatusCode);

            var refused = await Assert.ThrowsAsync<OctorateAuthException>(
                () => h.Client.SendHostReplyAsync("585001", "HMGRANTED1", "Grazie!", CancellationToken.None));
            Assert.True(refused.IsReviewNotAvailable);
        }

        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task SendHostReply_AnEmptyTextOrAnIdTheClientCannotTake_NeverLeaves()
    {
        var h = new ClientHarness();

        using (h.Scope.Use(OrgA))
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => h.Client.SendHostReplyAsync("585001", "HMGRANTED1", "   ", CancellationToken.None));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => h.Client.SendHostReplyAsync(string.Empty, "HMGRANTED1", "Grazie!", CancellationToken.None));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => h.Client.SendHostReplyAsync("585001", " ", "Grazie!", CancellationToken.None));
        }

        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task SendHostReview_PostsTheRatingsAndTheTexts_ToTheHostReviewPath()
    {
        var h = new ClientHarness();
        h.Answer(HttpStatusCode.OK, "{}");
        var review = new OctorateHostReviewCommand([new OctorateReviewRating("cleanliness", 5m), new OctorateReviewRating("communication", 4m)], "Ospiti ordinati", "Grazie");

        using (h.Scope.Use(OrgA))
            await h.Client.SendHostReviewAsync("585001", "HMGRANTED1", review, CancellationToken.None);

        var request = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/rest/v2/review/585001/HMGRANTED1/sendHostReview", request.Uri.PathAndQuery, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("Ospiti ordinati", body.RootElement.GetProperty("publicReview").GetString());
        Assert.Equal("Grazie", body.RootElement.GetProperty("privateReview").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("categoryRatings").GetArrayLength());
    }

    private sealed class ClientHarness
    {
        public ClientHarness()
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));
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
