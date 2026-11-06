using System.Net;
using System.Text;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E07/F06/US02/T02 (NF-94, D24): the Octorate operation that gives the contact of a guest. The data is sensitive and Octorate releases it only to
/// the registered IP addresses of CasaZen, so what is proven is that a refusal is never read as "the guest has no contact", that the call goes to the
/// right address for the org of the scope with an empty JSON body, and that a reply with nothing in it is an empty contact, not an error. The HTTP
/// answer is a stub: this is not a proof of Octorate.
/// </summary>
public class OctorateGuestContactInfoClientTests
{
    private static readonly Guid Org = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a01");

    /// <summary>
    /// Proof test (permission rule, AC-NF94-6): a 401 or a 403 of the contact read (no static IP registered, no permission) is
    /// <see cref="OctorateSensitiveDataNotAllowedException"/> and never an empty contact; another failure keeps its own type.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task OctorateClient_GuestContactInfo_RefusalIsNeverAnEmptyContact_Test(HttpStatusCode status)
    {
        var h = new Harness(status, """{"type":"ApiSecurityException","message":"mario.rossi@example.com"}""");

        using (h.Scope.Use(Org))
        {
            var refused = await Assert.ThrowsAsync<OctorateSensitiveDataNotAllowedException>(
                () => h.Client.GetGuestContactInfoAsync("585492", "guest-77", CancellationToken.None));

            Assert.Equal("octorate_sensitive_data_not_allowed", refused.Code);
            Assert.Equal(409, refused.ResponseStatusCode);
            Assert.Equal((int)status, refused.OctorateStatusCode);

            // Nothing of the body of the refusal reaches the message of the error.
            Assert.DoesNotContain("example", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        h.Handler.Status = HttpStatusCode.InternalServerError;
        using (h.Scope.Use(Org))
        {
            var failure = await Assert.ThrowsAsync<OctorateServerException>(
                () => h.Client.GetGuestContactInfoAsync("585492", "guest-77", CancellationToken.None));
            Assert.IsNotType<OctorateSensitiveDataNotAllowedException>(failure);
        }
    }

    [Fact]
    public async Task OctorateClient_GuestContactInfo_PostsToTheGuestOfTheAccommodationAndReadsTheContact()
    {
        var h = new Harness(HttpStatusCode.OK, """{"data":{"email":" guest@example.org ","phone":"+39 333 1234567"}}""");

        OctorateGuestContactInfo info;
        using (h.Scope.Use(Org))
            info = await h.Client.GetGuestContactInfoAsync("585492", "guest-77", CancellationToken.None);

        Assert.Equal("guest@example.org", info.Email);
        Assert.Equal("+39 333 1234567", info.Phone);
        Assert.False(info.IsEmpty);

        // One POST to the guest of the accommodation, for the org of the scope, with an empty JSON object as the body.
        var call = Assert.Single(h.Handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.octorate.test/connect/rest/v1/guests/585492/guest-77/contactInfo", call.Address.AbsoluteUri);
        Assert.Equal("Bearer token-" + Org.ToString("N"), call.Authorization);
        Assert.Equal("{}", call.Body);

        // The record says nothing of its data, even if a log line formats it.
        Assert.DoesNotContain("example", info.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234567", info.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer that has the members of a contact and leaves them empty (a null, an empty text, a blank one) is an empty contact that CasaZen
    /// understood: the guest has none to give.
    /// </summary>
    [Theory]
    [InlineData("""{"email":null,"phone":""}""")]
    [InlineData("""{"phone":null}""")]
    [InlineData("""{"data":{"mail":" "}}""")]
    public async Task OctorateClient_GuestContactInfo_AnAnswerWithNoContact_IsAnEmptyContact(string body)
    {
        var h = new Harness(HttpStatusCode.OK, body);

        using (h.Scope.Use(Org))
        {
            var info = await h.Client.GetGuestContactInfoAsync("585492", "guest-77", CancellationToken.None);

            Assert.True(info.IsEmpty);
            Assert.True(info.Recognized);
            Assert.Null(info.Email);
            Assert.Null(info.Phone);
        }
    }

    /// <summary>
    /// Proof test (the shape of the answer is not proven by the sandbox): an answer with none of the members that hold an address or a number (not an
    /// object, another wrapper, other names, a member that is an object) is not "the guest has no contact": the contact is empty and <c>Recognized</c> is
    /// false, so the service can say the answer is not understood instead of recording a guest with nothing to give.
    /// </summary>
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    [InlineData("""{"contactInfo":{"email":"guest@example.org"}}""")]
    [InlineData("""{"emails":["guest@example.org"],"phones":["+393331234567"]}""")]
    [InlineData("""{"email":{"address":"guest@example.org"}}""")]
    public async Task OctorateClient_GuestContactInfo_AnAnswerNotUnderstood_IsNeverAnEmptyContactOfAGuestWithNone(string body)
    {
        var h = new Harness(HttpStatusCode.OK, body);

        using (h.Scope.Use(Org))
        {
            var info = await h.Client.GetGuestContactInfoAsync("585492", "guest-77", CancellationToken.None);

            Assert.False(info.Recognized);
            Assert.True(info.IsEmpty);
            Assert.Null(info.Email);
            Assert.Null(info.Phone);
        }
    }

    [Fact]
    public async Task OctorateClient_GuestContactInfo_NoOrgInTheScope_MakesNoCall()
    {
        var h = new Harness(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.GetGuestContactInfoAsync("585492", "guest-77", CancellationToken.None));

        Assert.Empty(h.Handler.Calls);
    }

    private sealed class Harness
    {
        public Harness(HttpStatusCode status, string body)
        {
            Handler = new StubHandler { Status = status, Body = body };
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" })
                .Build();
            var tokens = new Mock<IOctorateTokenService>();
            tokens
                .Setup(t => t.GetAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid orgId, CancellationToken _) => "token-" + orgId.ToString("N"));
            Scope = new OctorateOrgScope(NullTenantContext.Instance);
            Client = new OctorateApiClient(
                new HttpClient(Handler),
                tokens.Object,
                new OctorateRateLimiter(clock),
                Scope,
                configuration,
                clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OctorateApiClient>.Instance);
        }

        public StubHandler Handler { get; }

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }
    }

    /// <summary>Answers every call with one status and one body, and keeps what was called.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; }

        public string Body { get; set; } = "{}";

        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString() ?? "(none)", body));
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record Call(HttpMethod Method, Uri Address, string Authorization, string Body);
}
