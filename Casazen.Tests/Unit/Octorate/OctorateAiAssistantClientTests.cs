using System.Net;
using System.Text;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E05/F05/US03/T03 (OCT-11, D4): the operation of the Octorate client that switches Octorate's own assistant on or off for one chat thread. The HTTP
/// answer is a stub: the sandbox has no chat license, so what is proven is what the client sends and how it reads the refusals, not Octorate (the way
/// the value travels is documented, not proven: the first licensed property tells). The rules that matter: the call says the value it sets (a repeat is
/// the same call), the refusals are the typed errors of every chat call, and nothing leaves for a thread that is not one.
/// </summary>
public class OctorateAiAssistantClientTests
{
    private static readonly Guid Org = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a07");

    private const string ServerPage = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"><html><head><title>Application server - Error report</title></head><body>403</body></html>";

    [Fact]
    public async Task SetThreadAiAssistant_PatchesTheThreadOfTheAccommodation_WithTheValueItSets_AndNoBody()
    {
        var h = new Harness();

        using (h.Scope.Use(Org))
        {
            await h.Client.SetThreadAiAssistantAsync("585492", "9001", false, CancellationToken.None);

            // A repeat is the same call, with the same value: the operation sets, it does not toggle.
            await h.Client.SetThreadAiAssistantAsync("585492", "9001", false, CancellationToken.None);
            await h.Client.SetThreadAiAssistantAsync("585492", "9001", true, CancellationToken.None);
        }

        Assert.Equal(3, h.Handler.Calls.Count);
        Assert.All(h.Handler.Calls, call =>
        {
            Assert.Equal(HttpMethod.Patch, call.Method);
            Assert.Equal("/connect/rest/v1/chat/585492/threads/9001/aiAssistant", call.Address.AbsolutePath);
            Assert.Equal("Bearer token-" + Org.ToString("N"), call.Authorization);
            Assert.Equal(string.Empty, call.Body);
        });
        Assert.Equal(
            [
                "https://api.octorate.test/connect/rest/v1/chat/585492/threads/9001/aiAssistant?enabled=false",
                "https://api.octorate.test/connect/rest/v1/chat/585492/threads/9001/aiAssistant?enabled=false",
                "https://api.octorate.test/connect/rest/v1/chat/585492/threads/9001/aiAssistant?enabled=true",
            ],
            h.Handler.Calls.Select(call => call.Address.AbsoluteUri));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task SetThreadAiAssistant_AnAcceptedCall_IsDone_AndNothingOfTheAnswerIsRead(HttpStatusCode status)
    {
        var h = new Harness();
        h.Handler.Respond = _ => (status, "this answer is not even JSON");

        using (h.Scope.Use(Org))
            await h.Client.SetThreadAiAssistantAsync("585492", "9001", false, CancellationToken.None);

        Assert.Single(h.Handler.Calls);
    }

    [Fact]
    public async Task SetThreadAiAssistant_ALicenseWithoutTheChatModule_IsChatNotLicensed()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.Forbidden, ServerPage);

        using (h.Scope.Use(Org))
        {
            var refused = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.SetThreadAiAssistantAsync("585492", "9001", false, CancellationToken.None));

            Assert.True(refused.IsChatNotLicensed);
            Assert.Equal("chat_not_licensed", refused.Code);
        }

        Assert.Single(h.Handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"type":"ApiNotFoundException"}""", typeof(OctorateNotFoundException))]
    [InlineData(HttpStatusCode.InternalServerError, """{"type":"ApiException","message":"boom"}""", typeof(OctorateServerException))]
    [InlineData(HttpStatusCode.BadRequest, """{"type":"ApiParamsException"}""", typeof(OctorateRequestRejectedException))]
    [InlineData(HttpStatusCode.Forbidden, """{"type":"ApiSecurityException"}""", typeof(OctorateAuthException))]
    public async Task SetThreadAiAssistant_TheOtherRefusals_AreTheTypedErrorsOfEveryCall(HttpStatusCode status, string body, Type expected)
    {
        var h = new Harness();
        h.Handler.Respond = _ => (status, body);

        using (h.Scope.Use(Org))
        {
            var error = await Assert.ThrowsAnyAsync<OctorateException>(() => h.Client.SetThreadAiAssistantAsync("585492", "9001", false, CancellationToken.None));

            Assert.IsType(expected, error);

            // A refusal that has a type of its own is the refusal of the API, not the page of a license without the chat module.
            Assert.False(error is OctorateAuthException { IsChatNotLicensed: true });
        }

        Assert.Single(h.Handler.Calls);
    }

    [Theory]
    [InlineData("585492", "not-a-number")]
    [InlineData("585492", "9001/../x")]
    [InlineData("585492", "")]
    [InlineData("", "9001")]
    public async Task SetThreadAiAssistant_AnIdThatIsNotOne_IsRefusedBeforeACall(string accommodation, string thread)
    {
        var h = new Harness();

        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.SetThreadAiAssistantAsync(accommodation, thread, false, CancellationToken.None));

        Assert.Empty(h.Handler.Calls);
    }

    [Fact]
    public async Task SetThreadAiAssistant_NoOrgInTheScope_MakesNoCall()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.SetThreadAiAssistantAsync("585492", "9001", false, CancellationToken.None));

        Assert.Empty(h.Handler.Calls);
    }

    [Fact]
    public async Task SetThreadAiAssistant_TheLogCarriesTheOperationAndTheStatus_NeverTheAnswer()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.InternalServerError, """{"type":"ApiException","message":"Giulietta Verdi giulietta.verdi@example.com"}""");

        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.SetThreadAiAssistantAsync("585492", "9001", false, CancellationToken.None));

        Assert.NotEmpty(h.Log.Entries);
        Assert.Contains("chat-ai-assistant", h.Log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Giulietta", h.Log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", h.Log.AllOutput, StringComparison.Ordinal);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" })
                .Build();
            var tokens = new Mock<IOctorateTokenService>();
            tokens
                .Setup(t => t.GetAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid orgId, CancellationToken _) => "token-" + orgId.ToString("N"));
            Scope = new OctorateOrgScope(NullTenantContext.Instance);
            Client = new OctorateApiClient(new HttpClient(Handler), tokens.Object, new OctorateRateLimiter(clock), Scope, configuration, clock, Log);
        }

        public StubHandler Handler { get; } = new();

        public CapturingLogger<OctorateApiClient> Log { get; } = new();

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }
    }

    /// <summary>Answers each call as <see cref="Respond"/> says, and keeps what was called.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> Respond { get; set; } = _ => (HttpStatusCode.OK, "{}");

        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString() ?? "(none)", body));
            var (status, answer) = Respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record Call(HttpMethod Method, Uri Address, string Authorization, string Body);
}
