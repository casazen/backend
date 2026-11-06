using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E05/F02/US02: the operations of the Octorate client that the messages hub writes with and reads helpers from: the message to a channel, the contact of
/// the booker of ONE reservation, the templates of a thread and the translation of a message. The HTTP answer is a stub: the sandbox has no chat license, so
/// what is proven is what the client sends and does with the shapes of the OpenAPI, not Octorate. The rules that matter: a send is made once and never
/// repeated by the client, a thread that cannot be written to is refused before anything leaves, the words are never logged, and a refusal of the contact is
/// never an empty contact.
/// </summary>
public class OctorateMessagesClientTests
{
    private static readonly Guid Org = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a01");

    private const string Thread = """
        { "id": 9001, "accommodationId": "585492", "defaultProcessor": "CHANNEL",
          "persons": [
            { "id": 2, "guestId": 77, "externalGuest": "A-1", "firstname": "Giulietta" },
            { "id": 1, "userId": 55, "firstname": "Marco" }
          ],
          "chatThreadExternals": [ { "id": 1, "threadId": 9001, "connectionId": 55, "externalId": "ext-thread-abc" } ] }
        """;

    private const string ServerPage = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"><html><head><title>Application server - Error report</title></head><body>403</body></html>";

    // ---- The message to a channel -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendChatMessage_ReadsTheThreadFirst_ThenPostsTheMessageToTheChannel()
    {
        var h = new Harness();
        h.Handler.Respond = request => request.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, Thread)
            : (HttpStatusCode.OK, """{ "id": 70010, "status": "CREATED" }""");

        OctorateChatSendResult sent;
        using (h.Scope.Use(Org))
            sent = await h.Client.SendChatMessageAsync("585492", "9001", "Benvenuti, vi aspettiamo", CancellationToken.None);

        Assert.Equal("70010", sent.MessageId);
        Assert.Equal(OtaMessageDeliveryState.Created, sent.State);
        Assert.Equal(2, h.Handler.Calls.Count);
        Assert.Equal(HttpMethod.Get, h.Handler.Calls[0].Method);
        Assert.Equal("/connect/rest/v1/chat/585492/threads/9001", h.Handler.Calls[0].Address.AbsolutePath);

        var post = h.Handler.Calls[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("/connect/rest/v1/chat/585492/messages", post.Address.AbsolutePath);
        Assert.Equal("Bearer token-" + Org.ToString("N"), post.Authorization);
        using var body = JsonDocument.Parse(post.Body);
        var root = body.RootElement;
        Assert.Equal(9001, root.GetProperty("threadId").GetInt64());
        Assert.Equal("ext-thread-abc", root.GetProperty("externalThreadId").GetString());
        Assert.Equal("CHANNEL", root.GetProperty("processor").GetString());
        Assert.Equal("CREATED", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("sender").GetProperty("id").GetInt64());
        Assert.True(root.TryGetProperty("createTime", out _));
        var attributes = root.GetProperty("attributes").EnumerateArray().ToDictionary(a => a.GetProperty("type").GetString()!, a => a.GetProperty("value").GetString()!);
        Assert.Equal("Benvenuti, vi aspettiamo", attributes["TEXT"]);
        Assert.Equal("TO", attributes["OTA_FLOW_DIRECTION"]);
        Assert.Equal(2, attributes.Count);
    }

    /// <summary>
    /// PROOF TEST: data rule (OCT-13). A write is never repeated by the client: a 5xx and a timeout are the typed error after ONE post, whatever the answer, and
    /// an answer without an id is not a success either (the message may exist).
    /// </summary>
    [Theory]
    [InlineData("500")]
    [InlineData("timeout")]
    [InlineData("noid")]
    public async Task SendChatMessage_AnAmbiguousOutcome_IsTheTypedError_AfterOnePost(string outcome)
    {
        var h = new Harness();
        h.Handler.Respond = request =>
        {
            if (request.Method == HttpMethod.Get)
                return (HttpStatusCode.OK, Thread);

            return outcome switch
            {
                "500" => (HttpStatusCode.InternalServerError, """{"type":"ApiException","message":"boom"}"""),
                "timeout" => throw new TaskCanceledException("timed out"),
                _ => (HttpStatusCode.OK, """{ "status": "SENT" }"""),
            };
        };

        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.SendChatMessageAsync("585492", "9001", "Ciao", CancellationToken.None));

        Assert.Single(h.Handler.Calls, call => call.Method == HttpMethod.Post);
    }

    /// <summary>
    /// PROOF TEST: data rule. A failure of the READ of the thread, which comes before the post (a 5xx, a timeout, an answer that cannot be read), is not the
    /// outcome nobody knows: nothing was posted, and the error says so (<see cref="OctorateChatNotReadException"/>), so the caller can send the message again.
    /// </summary>
    [Theory]
    [InlineData("500")]
    [InlineData("timeout")]
    [InlineData("unreadable")]
    public async Task SendChatMessage_AFailureOfTheReadOfTheThread_IsNotTheAmbiguousOutcome_AndNothingIsPosted_Test(string failure)
    {
        var h = new Harness();
        h.Handler.Respond = _ => failure switch
        {
            "500" => (HttpStatusCode.InternalServerError, """{"type":"ApiException","message":"boom"}"""),
            "timeout" => throw new TaskCanceledException("timed out"),
            _ => (HttpStatusCode.OK, "<html>not json</html>"),
        };

        using (h.Scope.Use(Org))
        {
            var error = await Assert.ThrowsAsync<OctorateChatNotReadException>(() => h.Client.SendChatMessageAsync("585492", "9001", "Ciao", CancellationToken.None));
            Assert.Equal("octorate_unavailable", error.Code);
            Assert.Equal(502, error.ResponseStatusCode);
        }

        Assert.All(h.Handler.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task SendChatMessage_AThreadWithNoPersonOfTheHostSide_OrNoIdOnTheChannel_IsRefusedBeforeAnythingIsSent()
    {
        var h = new Harness();
        using (h.Scope.Use(Org))
        {
            h.Handler.Respond = _ => (HttpStatusCode.OK, """{ "id": 9001, "persons": [ { "id": 2, "guestId": 77 } ], "chatThreadExternals": [ { "externalId": "x" } ] }""");
            await Assert.ThrowsAsync<OctorateChatNotSendableException>(() => h.Client.SendChatMessageAsync("585492", "9001", "Ciao", CancellationToken.None));

            h.Handler.Respond = _ => (HttpStatusCode.OK, """{ "id": 9001, "persons": [ { "id": 1, "userId": 55 } ], "chatThreadExternals": [] }""");
            var error = await Assert.ThrowsAsync<OctorateChatNotSendableException>(() => h.Client.SendChatMessageAsync("585492", "9001", "Ciao", CancellationToken.None));
            Assert.Equal("octorate_chat_not_sendable", error.Code);
            Assert.Equal(409, error.ResponseStatusCode);
        }

        Assert.All(h.Handler.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task SendChatMessage_AStateOctorateDoesNotSpell_IsAMessageToVerify_NeverASuccess()
    {
        var h = new Harness();
        h.Handler.Respond = request => request.Method == HttpMethod.Get ? (HttpStatusCode.OK, Thread) : (HttpStatusCode.OK, """{ "id": "70011", "status": "DELIVERING" }""");

        OctorateChatSendResult sent;
        using (h.Scope.Use(Org))
            sent = await h.Client.SendChatMessageAsync("585492", "9001", "Ciao", CancellationToken.None);

        Assert.Equal("70011", sent.MessageId);
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, sent.State);
    }

    [Theory]
    [InlineData("SENT", OtaMessageDeliveryState.Sent)]
    [InlineData("WAIT_AUTH", OtaMessageDeliveryState.WaitingAuthorization)]
    [InlineData("ERROR", OtaMessageDeliveryState.Error)]
    [InlineData("MANUAL_VERIFY", OtaMessageDeliveryState.ManualVerify)]
    public async Task SendChatMessage_TheStateOctorateReports_IsTheStateOfTheMessage(string status, OtaMessageDeliveryState expected)
    {
        var h = new Harness();
        h.Handler.Respond = request => request.Method == HttpMethod.Get ? (HttpStatusCode.OK, Thread) : (HttpStatusCode.OK, $$"""{ "data": { "id": 70012, "status": "{{status}}" } }""");

        using (h.Scope.Use(Org))
            Assert.Equal(expected, (await h.Client.SendChatMessageAsync("585492", "9001", "Ciao", CancellationToken.None)).State);
    }

    [Fact]
    public async Task SendChatMessage_ALicenseWithoutTheChatModule_IsChatNotLicensed()
    {
        var h = new Harness();
        h.Handler.Respond = request => request.Method == HttpMethod.Get ? (HttpStatusCode.OK, Thread) : (HttpStatusCode.Forbidden, ServerPage);

        using (h.Scope.Use(Org))
        {
            var refused = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.SendChatMessageAsync("585492", "9001", "Ciao", CancellationToken.None));
            Assert.True(refused.IsChatNotLicensed);
        }
    }

    /// <summary>PROOF TEST: privacy rule. The words of a message are in the request and in nothing the client logs, on success and on every failure.</summary>
    [Fact]
    public async Task SendChatMessage_TheWordsAreNeverLogged_Test()
    {
        var h = new Harness();
        const string words = "PAROLE-SEGRETE dell'ospite Anna Rossi 333 123 4567";
        using (h.Scope.Use(Org))
        {
            h.Handler.Respond = request => request.Method == HttpMethod.Get ? (HttpStatusCode.OK, Thread) : (HttpStatusCode.OK, """{ "id": 1, "status": "SENT" }""");
            await h.Client.SendChatMessageAsync("585492", "9001", words, CancellationToken.None);

            h.Handler.Respond = request => request.Method == HttpMethod.Get ? (HttpStatusCode.OK, Thread) : (HttpStatusCode.InternalServerError, """{"type":"ApiException"}""");
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.SendChatMessageAsync("585492", "9001", words, CancellationToken.None));

            h.Handler.Respond = request => request.Method == HttpMethod.Get ? (HttpStatusCode.OK, Thread) : (HttpStatusCode.OK, "{}");
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.SendChatMessageAsync("585492", "9001", words, CancellationToken.None));
        }

        Assert.NotEmpty(h.Log.Entries);
        Assert.DoesNotContain("PAROLE", h.Log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Rossi", h.Log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("333 123", h.Log.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void NewMessageBody_NeedsTheThreadToNameTheSenderAndTheChannelThread_AsNumbersOfTheSpecification()
    {
        var complete = new OctorateChatThread { Id = "9001", ExternalThreadId = "x", HostPersonId = "1" };
        var now = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

        Assert.NotNull(OctorateChatRequests.NewMessageBody("9001", complete, "Ciao", now));
        Assert.Contains("\"createTime\":\"2026-11-06T08:00:00.000Z\"", OctorateChatRequests.NewMessageBody("9001", complete, "Ciao", now), StringComparison.Ordinal);
        Assert.Null(OctorateChatRequests.NewMessageBody("9001", complete with { ExternalThreadId = null }, "Ciao", now));
        Assert.Null(OctorateChatRequests.NewMessageBody("9001", complete with { HostPersonId = null }, "Ciao", now));
        Assert.Null(OctorateChatRequests.NewMessageBody("9001", complete with { HostPersonId = "abc" }, "Ciao", now));
        Assert.Null(OctorateChatRequests.NewMessageBody("not-a-number", complete, "Ciao", now));
    }

    [Fact]
    public async Task ReadThread_NamesThePersonOfTheHostSide_AndTheThreadOnTheChannel()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, Thread);

        OctorateChatThread thread;
        using (h.Scope.Use(Org))
            thread = await h.Client.GetChatThreadAsync("585492", "9001", CancellationToken.None);

        Assert.Equal("1", thread.HostPersonId);
        Assert.Equal("ext-thread-abc", thread.ExternalThreadId);
    }

    // ---- The contact of the booker of one reservation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ReservationContactInfo_PostsToTheReservationOfTheAccommodation_WithNoBody_AndReadsTheContact()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, """{ "refer": "REF-77", "bookerGivenName": "Mei", "bookerFamilyName": "Tanaka", "email": " mei@guest-mail.example ", "phone": "+81 90 1234 5678" }""");

        OctorateGuestContactInfo info;
        using (h.Scope.Use(Org))
            info = await h.Client.GetReservationContactInfoAsync("585492", "REF-77", CancellationToken.None);

        Assert.Equal("mei@guest-mail.example", info.Email);
        Assert.Equal("+81 90 1234 5678", info.Phone);
        Assert.True(info.Recognized);
        var call = Assert.Single(h.Handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.octorate.test/connect/rest/v2/reservation/585492/REF-77/contactInfo", call.Address.AbsoluteUri);
        Assert.Equal(string.Empty, call.Body);
        Assert.DoesNotContain("guest-mail", info.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PROOF TEST: permission rule (S7, D24). A 401 or a 403 of the contact is never an empty contact; a 5xx stays a failure, not a refusal.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ReservationContactInfo_ARefusal_IsNeverAnEmptyContact_Test(HttpStatusCode status)
    {
        var h = new Harness();
        h.Handler.Respond = _ => (status, """{"type":"ApiSecurityException","message":"mei@guest-mail.example"}""");

        using (h.Scope.Use(Org))
        {
            var refused = await Assert.ThrowsAsync<OctorateSensitiveDataNotAllowedException>(() => h.Client.GetReservationContactInfoAsync("585492", "REF-77", CancellationToken.None));
            Assert.Equal("octorate_sensitive_data_not_allowed", refused.Code);
            Assert.DoesNotContain("guest-mail", refused.Message, StringComparison.OrdinalIgnoreCase);

            h.Handler.Respond = _ => (HttpStatusCode.InternalServerError, "{}");
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetReservationContactInfoAsync("585492", "REF-77", CancellationToken.None));
        }

        Assert.DoesNotContain("guest-mail", h.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{"contactInfo":{"email":"mei@guest-mail.example"}}""")]
    public async Task ReservationContactInfo_AnAnswerNotUnderstood_IsNotAGuestWithNoContact(string body)
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, body);

        using (h.Scope.Use(Org))
        {
            var info = await h.Client.GetReservationContactInfoAsync("585492", "REF-77", CancellationToken.None);

            Assert.False(info.Recognized);
            Assert.True(info.IsEmpty);
        }
    }

    [Fact]
    public async Task ReservationContactInfo_NoOrgInTheScope_MakesNoCall()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.GetReservationContactInfoAsync("585492", "REF-77", CancellationToken.None));

        Assert.Empty(h.Handler.Calls);
    }

    // ---- The templates and the translation --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChatTemplates_AreReadFromTheThread_AsAListOrAWrapper_AndAnItemWithNoBodyIsLeftOut()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, """
            { "data": [
                { "id": 11, "name": "Conferma prenotazione", "language": "it", "text": "Grazie per la prenotazione" },
                { "id": 12, "name": "Vuoto", "language": "EN", "text": "  " },
                { "name": "Senza id", "text": "Ciao" },
                { "id": 14, "text": "Solo il testo" }
            ] }
            """);

        OctorateChatTemplate[] templates;
        using (h.Scope.Use(Org))
            templates = await h.Client.GetChatTemplatesAsync("585492", "9001", CancellationToken.None);

        Assert.Equal("/connect/rest/v1/chat/585492/threads/9001/templates", Assert.Single(h.Handler.Calls).Address.AbsolutePath);
        Assert.Equal(["11", "14"], templates.Select(t => t.Id));
        Assert.Equal(new OctorateChatTemplate("11", "Conferma prenotazione", "IT", "Grazie per la prenotazione"), templates[0]);
        Assert.Equal("14", templates[1].Name);

        h.Handler.Respond = _ => (HttpStatusCode.OK, """{"unexpected": true}""");
        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetChatTemplatesAsync("585492", "9001", CancellationToken.None));
    }

    [Fact]
    public async Task TranslateChatMessage_PatchesTheMessage_AndReadsTheTranslationOfTheLanguage()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, """
            { "id": 70001, "threadId": 9001, "attributes": [
                { "type": "TEXT", "value": "Ciao" },
                { "type": "TRANSLATION_MAP", "value": "{\"EN\":\"Hello\",\"FR\":\"Bonjour\"}" }
            ] }
            """);

        string translated;
        using (h.Scope.Use(Org))
            translated = await h.Client.TranslateChatMessageAsync("585492", "9001", "70001", "en", CancellationToken.None);

        Assert.Equal("Hello", translated);
        var call = Assert.Single(h.Handler.Calls);
        Assert.Equal(HttpMethod.Patch, call.Method);
        Assert.Equal("/connect/rest/v1/chat/585492/threads/9001/messages/70001/translate/EN", call.Address.AbsolutePath);
    }

    [Theory]
    [InlineData("""{ "id": 70001, "attributes": [ { "type": "TEXT", "value": "Ciao" } ] }""")]
    [InlineData("""{ "id": 70001, "attributes": [ { "type": "TRANSLATION_MAP", "value": "not json" } ] }""")]
    [InlineData("""{ "id": 70001, "attributes": [ { "type": "TRANSLATION_MAP", "value": "{\"FR\":\"Bonjour\"}" } ] }""")]
    public async Task TranslateChatMessage_AnAnswerWithNoTranslationInThatLanguage_IsNotATranslation(string body)
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, body);

        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.TranslateChatMessageAsync("585492", "9001", "70001", "EN", CancellationToken.None));
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("")]
    [InlineData("E")]
    public async Task TranslateChatMessage_ALanguageOutsideThe14_AndAnIdThatIsNotANumber_AreRefusedBeforeACall(string language)
    {
        var h = new Harness();

        using (h.Scope.Use(Org))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.TranslateChatMessageAsync("585492", "9001", "70001", language, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => h.Client.TranslateChatMessageAsync("585492", "9001", "70001/../x", "EN", CancellationToken.None));
        }

        Assert.Empty(h.Handler.Calls);
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

    /// <summary>Answers each call as <see cref="Respond"/> says (or throws what it throws), and keeps what was called.</summary>
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
