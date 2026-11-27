using System.Net;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The chat reads of the Octorate client (E05/F01/US01/T02): the four operations, their endpoints and the shapes they read, and the one refusal that
/// is the chat's own, a license without the <c>chat</c> module (ADR-023-R6). The HTTP answer is a stub: the sandbox has no chat license, so what is
/// proven is what the client does with the shapes of the OpenAPI, not Octorate.
/// </summary>
public class OctorateChatClientTests
{
    private static readonly Guid Org = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a01");

    private const string ServerPage = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"><html><head><title>Application server - Error report</title></head><body>403</body></html>";

    private const string ServerErrorPage = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"><html><head><title>Application server - Error report</title></head><body>500</body></html>";

    /// <summary>
    /// PROOF TEST: trust rule (ADR-023-R6, AC1 of T02). A 403 that is not an error of Octorate's own API, the page of the application server which is
    /// what a license without the chat module gets, is <c>chat_not_licensed</c> for the four chat reads, decided by the code and the shape of the
    /// answer and never by its text. A typed 403 keeps its common meaning (a refused token, the wrong environment), and the same page on any
    /// other operation is not a chat matter.
    /// </summary>
    [Fact]
    public async Task OctorateClient_ChatRead_403WithTheServerPage_IsChatNotLicensed_Test()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.Forbidden, ServerPage);

        using (h.Scope.Use(Org))
        {
            var reads = new Func<Task>[]
            {
                () => h.Client.GetChatThreadsAsync("585492", 0, CancellationToken.None),
                () => h.Client.GetChatThreadAsync("585492", "9001", CancellationToken.None),
                () => h.Client.GetChatMessagesAsync("585492", "9001", null, CancellationToken.None),
                () => h.Client.GetRecentChatMessagesAsync("585492", CancellationToken.None),
            };
            foreach (var read in reads)
            {
                var refused = await Assert.ThrowsAsync<OctorateAuthException>(read);
                Assert.Equal("chat_not_licensed", refused.Code);
                Assert.True(refused.IsChatNotLicensed);
                Assert.Equal(403, refused.OctorateStatusCode);
                Assert.Equal(409, refused.ResponseStatusCode);
                Assert.Equal("OctorateChatNotLicensed", refused.MessageKey);
            }

            // A typed refusal of the API is not the license: a token the API does not accept, the other environment.
            h.Handler.Respond = _ => (HttpStatusCode.Forbidden, """{"type":"ApiSecurityException","message":"No chat for you","suggestion":"errRefreshTokenInvalid"}""");
            var typed = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.GetChatThreadsAsync("585492", 0, CancellationToken.None));
            Assert.Equal("octorate_auth_failed", typed.Code);

            h.Handler.Respond = _ => (HttpStatusCode.Forbidden, """{"type":"WrongEnviroment"}""");
            await Assert.ThrowsAsync<OctorateWrongEnvironmentException>(() => h.Client.GetChatThreadsAsync("585492", 0, CancellationToken.None));

            // The page of the application server on a call that is not a chat call keeps the common meaning.
            h.Handler.Respond = _ => (HttpStatusCode.Forbidden, ServerPage);
            var other = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.GetPropertyAsync("585492", CancellationToken.None));
            Assert.Equal("octorate_auth_failed", other.Code);

            // A failure of Octorate is a failure, not a license: the reads that answer 403 without the module do not answer 500 for it.
            h.Handler.Respond = _ => (HttpStatusCode.InternalServerError, ServerErrorPage);
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetChatThreadsAsync("585492", 0, CancellationToken.None));
        }
    }

    /// <summary>
    /// PROOF TEST: trust rule (ADR-023-R6, AC1 of T02; <c>sandbox-run-results.md</c>: "E05/F01 must treat non-JSON 403 and 500 as chat not available").
    /// Without the chat module the sandbox answers <c>recentMessages</c>, the first call of every pass of the reconciliation, with a 500 that is not
    /// an error of Octorate's API: that is <c>chat_not_licensed</c> (the listing is kept as a state and rests), not a failure tried again every ten
    /// minutes. A 500 that carries a <c>type</c> of the API, another 5xx and a 500 on the other chat reads keep their common meaning.
    /// </summary>
    [Fact]
    public async Task OctorateClient_RecentChatMessages_500WithTheServerPage_IsChatNotLicensed_Test()
    {
        var h = new Harness();
        using (h.Scope.Use(Org))
        {
            h.Handler.Respond = _ => (HttpStatusCode.InternalServerError, ServerErrorPage);
            var refused = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.GetRecentChatMessagesAsync("585492", CancellationToken.None));
            Assert.Equal("chat_not_licensed", refused.Code);
            Assert.True(refused.IsChatNotLicensed);
            Assert.Equal(500, refused.OctorateStatusCode);
            Assert.Equal(409, refused.ResponseStatusCode);
            Assert.Equal("OctorateChatNotLicensed", refused.MessageKey);

            // The 403 of the application server is still the license for this read, as for the others.
            h.Handler.Respond = _ => (HttpStatusCode.Forbidden, ServerPage);
            var forbidden = await Assert.ThrowsAsync<OctorateAuthException>(() => h.Client.GetRecentChatMessagesAsync("585492", CancellationToken.None));
            Assert.True(forbidden.IsChatNotLicensed);

            // A failure the API itself describes (it has a type) and another 5xx are failures of Octorate, not a license.
            h.Handler.Respond = _ => (HttpStatusCode.InternalServerError, """{"type":"ApiException","message":"boom"}""");
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetRecentChatMessagesAsync("585492", CancellationToken.None));

            h.Handler.Respond = _ => (HttpStatusCode.ServiceUnavailable, ServerErrorPage);
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetRecentChatMessagesAsync("585492", CancellationToken.None));

            // The other reads answer 403 without the module: a 500 on them is a failure.
            h.Handler.Respond = _ => (HttpStatusCode.InternalServerError, ServerErrorPage);
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetChatThreadAsync("585492", "9001", CancellationToken.None));
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetChatMessagesAsync("585492", "9001", null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task OctorateClient_ChatThreads_AreAskedByOffsetAndReadFromTheDto()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, """
            [
              {
                "id": 9001, "accommodationId": "585492", "defaultProcessor": "CHANNEL", "aiDriven": false,
                "persons": [
                  { "id": 1, "userId": 55, "firstname": "Marco", "lastname": "Host" },
                  { "id": 2, "guestId": 77, "externalGuest": "A-1", "firstname": "Giulietta", "lastname": "Verdi" }
                ],
                "lastMessage": { "id": 70001, "createTime": "2026-11-06T07:00:00.000Z" },
                "chatThreadExternals": [ { "id": 1, "threadId": 9001, "connectionId": 55, "externalId": "abc" } ],
                "attributes": [ { "type": "AIRBNB_BOOKING_DETAILS", "value": "{}" } ],
                "reservation": { "id": 4711, "channelId": 274, "channelName": "Airbnb" },
                "archiveTime": null
              },
              { "accommodationId": "585492" }
            ]
            """);

        OctorateChatThreadPage page;
        using (h.Scope.Use(Org))
            page = await h.Client.GetChatThreadsAsync("585492", 40, CancellationToken.None);

        // The list is asked by position, all threads whatever their read state; the item with no id is counted and left out.
        var address = Assert.Single(h.Handler.Addresses);
        Assert.Equal("/connect/rest/v1/chat/585492/threads", address.AbsolutePath);
        Assert.Equal("?offset=40&filterReadStatus=false", address.Query);
        Assert.Equal(2, page.ItemCount);
        Assert.Equal(1, page.Unreadable);
        var thread = Assert.Single(page.Threads);
        Assert.Equal("9001", thread.Id);
        Assert.Equal("585492", thread.AccommodationId);
        Assert.Equal("CHANNEL", thread.DefaultProcessor);
        Assert.False(thread.AiDriven);
        Assert.Equal("4711", thread.ReservationId);
        Assert.Equal("274", thread.ReservationChannelId);
        Assert.Equal(["55"], thread.ConnectionIds);
        Assert.True(thread.HasAirbnbBookingDetails);
        Assert.Equal("Giulietta Verdi", thread.GuestName);
        Assert.Equal(new DateTime(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc), thread.LastActivityAt);
        Assert.Null(thread.ArchivedAt);

        // An answer that is not a list is an answer CasaZen cannot read, never a list with nothing in it.
        h.Handler.Respond = _ => (HttpStatusCode.OK, """{"unexpected": true}""");
        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetChatThreadsAsync("585492", 0, CancellationToken.None));
    }

    [Fact]
    public async Task OctorateClient_ChatMessages_AreReadFromTheDtoAndAllPagesAreFollowed()
    {
        var h = new Harness();
        h.Handler.Respond = uri => uri.Query.Contains("page=1", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """
              { "page": { "page": 1, "totalPages": 2 }, "data": [
                { "id": 70003, "threadId": 9001, "createTime": "2026-11-06T07:06:00.000Z", "status": "CREATED", "processor": "NOTE",
                  "sender": { "id": 1, "userId": 55 }, "attributes": [ { "type": "TEXT", "value": "nota interna" } ] }
              ] }
              """)
            : (HttpStatusCode.OK, """
              { "page": { "page": 0, "totalPages": 2 }, "data": [
                { "id": 70001, "threadId": 9001, "createTime": "2026-11-06T07:00:00.000Z", "sentTime": "2026-11-06T07:00:01.000Z",
                  "status": "SENT", "processor": "CHANNEL",
                  "sender": { "id": 2, "guestId": 77, "externalGuest": "A-1", "firstname": "Giulietta", "lastname": "Verdi" },
                  "attributes": [ { "type": "TEXT", "value": "Buongiorno" } ],
                  "readers": [ { "id": 1, "messageId": 70001, "readTime": "2026-11-06T07:30:00.000Z" } ],
                  "attachment": { "id": 1, "name": "foto.jpg", "type": "JPEG", "url": "https://resx.example.test/foto.jpg" } },
                { "id": 70002, "threadId": 9001, "createTime": "2026-11-06T07:05:00.000Z", "status": "WAIT_AUTH", "processor": "CHANNEL",
                  "sender": { "id": 1, "userId": 55, "firstname": "Marco", "lastname": "Host" },
                  "attributes": [ { "type": "TEXT", "value": "Benvenuti" }, { "type": "OTA_FLOW_DIRECTION", "value": "TO" } ] },
                { "id": 70004, "createTime": "not a time", "status": "SENT" }
              ] }
              """);

        OctorateChatMessage[] messages;
        using (h.Scope.Use(Org))
            messages = await h.Client.GetChatMessagesAsync("585492", "9001", new DateTime(2026, 11, 6, 6, 55, 0, DateTimeKind.Utc), CancellationToken.None);

        // createdAfter goes as an ISO instant; the second page is asked for by its number.
        Assert.Equal(2, h.Handler.Addresses.Count);
        Assert.Equal("/connect/rest/v1/chat/585492/threads/9001/messages", h.Handler.Addresses[0].AbsolutePath);
        Assert.Equal("createdAfter=2026-11-06T06:55:00.000Z", Uri.UnescapeDataString(h.Handler.Addresses[0].Query.TrimStart('?')));
        Assert.Equal("createdAfter=2026-11-06T06:55:00.000Z&page=1", Uri.UnescapeDataString(h.Handler.Addresses[1].Query.TrimStart('?')));

        // The item with no usable time is left out; the others are read with their direction, state, words and attachment.
        Assert.Equal(["70001", "70002", "70003"], messages.Select(m => m.Id));
        var guest = messages[0];
        Assert.Equal(OtaMessageDirection.Inbound, guest.Direction);
        Assert.Equal(OtaMessageDeliveryState.Sent, guest.DeliveryState);
        Assert.Equal("Giulietta Verdi", guest.SenderName);
        Assert.Equal("Buongiorno", guest.Text);
        Assert.Equal(new DateTime(2026, 11, 6, 7, 0, 1, DateTimeKind.Utc), guest.SentAt);
        Assert.Equal(new DateTime(2026, 11, 6, 7, 30, 0, DateTimeKind.Utc), guest.ReadAt);
        Assert.Equal(("https://resx.example.test/foto.jpg", "image/jpeg"), (guest.AttachmentUrl, guest.AttachmentContentType));
        Assert.Equal("9001", guest.ThreadId);

        var host = messages[1];
        Assert.Equal(OtaMessageDirection.Outbound, host.Direction);
        Assert.Equal(OtaMessageDeliveryState.WaitingAuthorization, host.DeliveryState);
        Assert.Equal("Benvenuti", host.Text);
        Assert.Equal(new DateTime(2026, 11, 6, 7, 5, 0, DateTimeKind.Utc), host.SentAt);
        Assert.Null(host.AttachmentUrl);
        Assert.Equal("NOTE", messages[2].Processor);

        // A wrapper that announces more pages and gives the same page again ignores the page asked for: the thread was not read whole.
        h.Handler.Addresses.Clear();
        h.Handler.Respond = _ => (HttpStatusCode.OK, """
            { "page": { "page": 0, "totalPages": 3 }, "data": [
              { "id": 70001, "threadId": 9001, "createTime": "2026-11-06T07:00:00.000Z", "status": "SENT", "attributes": [ { "type": "TEXT", "value": "Ciao" } ] }
            ] }
            """);
        using (h.Scope.Use(Org))
            await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetChatMessagesAsync("585492", "9001", null, CancellationToken.None));
    }

    [Fact]
    public async Task OctorateClient_RecentChatMessages_AreAskedForTheAccommodationAndTheDirectionFollowsTheSender()
    {
        var h = new Harness();
        h.Handler.Respond = _ => (HttpStatusCode.OK, """
            [
              { "id": 1, "threadId": 9001, "createTime": "2026-11-06T07:00:00.000Z", "status": "SENT", "processor": "CHANNEL",
                "sender": { "id": 2 }, "externalSender": { "id": 3, "externalGuest": "A-1" }, "attributes": [ { "type": "TEXT", "value": "Dal portale" } ] },
              { "id": 2, "threadId": 9001, "createTime": "2026-11-06T07:01:00.000Z", "status": "SENT", "processor": "CHANNEL",
                "sender": { "id": 4 }, "attributes": [ { "type": "TEXT", "value": "Senza mittente noto" } ] },
              { "id": 3, "threadId": 9001, "createTime": "2026-11-06T07:02:00.000Z", "status": "SENT", "processor": "CHANNEL",
                "sender": { "id": 4 }, "attributes": [ { "type": "OTA_FLOW_DIRECTION", "value": "FROM" }, { "type": "TEXT", "value": "Il flusso lo dice" } ] },
              { "id": 4, "threadId": 9001, "createTime": "2026-11-06T07:03:00.000Z", "status": "BOH", "processor": "CHANNEL",
                "sender": { "id": 5, "userId": 55 }, "attributes": [ { "type": "TEXT", "value": "Stato sconosciuto" } ] }
            ]
            """);

        OctorateChatMessage[] messages;
        using (h.Scope.Use(Org))
            messages = await h.Client.GetRecentChatMessagesAsync("585492", CancellationToken.None);

        var address = Assert.Single(h.Handler.Addresses);
        Assert.Equal("/connect/rest/v1/accommodation/recentMessages", address.AbsolutePath);
        Assert.Equal("?property=585492", address.Query);

        // The portal's sender is the guest; a sender nothing places is the host's, never taken for the guest; the flow says it when it can;
        // a state Octorate does not spell is never read as sent for a message of the host.
        Assert.Equal(
            [OtaMessageDirection.Inbound, OtaMessageDirection.Outbound, OtaMessageDirection.Inbound, OtaMessageDirection.Outbound],
            messages.Select(m => m.Direction));
        Assert.Equal(OtaMessageDeliveryState.ManualVerify, messages[3].DeliveryState);
        Assert.Equal(OtaMessageDeliveryState.Sent, messages[0].DeliveryState);
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
            tokens.Setup(t => t.GetAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("token");
            Scope = new OctorateOrgScope(NullTenantContext.Instance);
            Client = new OctorateApiClient(
                new HttpClient(Handler),
                tokens.Object,
                new OctorateRateLimiter(clock),
                Scope,
                configuration,
                clock,
                NullLogger<OctorateApiClient>.Instance);
        }

        public StubHandler Handler { get; } = new();

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }
    }

    /// <summary>Answers each call as <see cref="Respond"/> says and keeps the addresses called.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Uri> Addresses { get; } = [];

        public Func<Uri, (HttpStatusCode Status, string Body)> Respond { get; set; } = _ => (HttpStatusCode.OK, "[]");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Addresses.Add(request.RequestUri!);
            var (status, body) = Respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
