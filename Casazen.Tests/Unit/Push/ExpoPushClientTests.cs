using System.Net;
using System.Text.Json;
using Casazen.Infrastructure.Push;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>MO-04 (A6-29): the Expo HTTP client, its access token and how it classifies Expo's answers.</summary>
public class ExpoPushClientTests
{
    [Fact]
    public async Task SendAsync_AccessTokenConfigured_SendsItAsBearer()
    {
        var handler = new StubHandler(_ => Ok("""{"data":[{"status":"ok","id":"t-1"}]}"""));

        await Client(handler, accessToken: " expo-access-token ").SendAsync([Message("ExponentPushToken[a]")]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal("expo-access-token", request.Authorization?.Parameter);
        Assert.Equal("https://exp.host/--/api/v2/push/send", request.Uri);
    }

    [Fact]
    public async Task SendAsync_NoAccessToken_SendsNoAuthorizationHeader()
    {
        var handler = new StubHandler(_ => Ok("""{"data":[{"status":"ok","id":"t-1"}]}"""));

        await Client(handler, accessToken: null).SendAsync([Message("ExponentPushToken[a]")]);

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task SendAsync_Batch_PostsEveryMessageAndReturnsTheTicketsInOrder()
    {
        var handler = new StubHandler(_ => Ok("""
            {"data":[
              {"status":"ok","id":"t-1"},
              {"status":"error","message":"\"ExponentPushToken[b]\" is not a registered push notification recipient","details":{"error":"DeviceNotRegistered"}}
            ]}
            """));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]"), Message("ExponentPushToken[b]")]);

        Assert.Equal(ExpoSendOutcome.Accepted, result.Outcome);
        Assert.Equal(
            [new ExpoPushTicket(true, "t-1", null), new ExpoPushTicket(false, null, "DeviceNotRegistered")],
            result.Tickets);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var messages = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(["ExponentPushToken[a]", "ExponentPushToken[b]"], messages.Select(m => m.GetProperty("to").GetString()));
        Assert.Equal("default", messages[0].GetProperty("channelId").GetString());
        Assert.Equal("/properties", messages[0].GetProperty("data").GetProperty("route").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ExpoSendOutcome.NotSent)]
    [InlineData(HttpStatusCode.InternalServerError, ExpoSendOutcome.NotSent)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ExpoSendOutcome.NotSent)]
    [InlineData(HttpStatusCode.Unauthorized, ExpoSendOutcome.Refused)]
    [InlineData(HttpStatusCode.BadRequest, ExpoSendOutcome.Refused)]
    public async Task SendAsync_HttpError_IsClassified(HttpStatusCode status, ExpoSendOutcome expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"errors":[{"code":"X","message":"m"}]}"""),
        });

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]")]);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal($"Http{(int)status}", result.Error);
        Assert.Empty(result.Tickets);
    }

    [Fact]
    public async Task SendAsync_ConnectionRefused_ReturnsNotSent()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]")]);

        Assert.Equal(ExpoSendOutcome.NotSent, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_ConnectionLostAfterTheRequest_ReturnsUnknown()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "ended"));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]")]);

        Assert.Equal(ExpoSendOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_Timeout_ReturnsUnknown()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]")]);

        Assert.Equal(ExpoSendOutcome.Unknown, result.Outcome);
        Assert.Equal("Timeout", result.Error);
    }

    [Fact]
    public async Task SendAsync_FewerTicketsThanMessages_ReturnsUnknown()
    {
        var handler = new StubHandler(_ => Ok("""{"data":[{"status":"ok","id":"t-1"}]}"""));

        var result = await Client(handler).SendAsync([Message("ExponentPushToken[a]"), Message("ExponentPushToken[b]")]);

        Assert.Equal(ExpoSendOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_MoreThan100Messages_Throws()
    {
        var handler = new StubHandler(_ => Ok("{}"));
        var messages = Enumerable.Range(0, 101).Select(i => Message($"ExponentPushToken[{i}]")).ToList();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client(handler).SendAsync(messages));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_RefusedTicket_LogsNoToken()
    {
        var logger = new CapturingLogger<ExpoPushClient>();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"errors":[{"code":"UNAUTHORIZED","message":"ExponentPushToken[secret]"}]}"""),
        });

        await new ExpoPushClient(Http(handler), Options.Create(new ExpoPushOptions()), logger)
            .SendAsync([Message("ExponentPushToken[secret]")]);

        Assert.Contains("Expo__AccessToken", logger.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", logger.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReceiptsAsync_Receipts_PostsTheIdsAndParsesOkAndErrors()
    {
        var handler = new StubHandler(_ => Ok("""
            {"data":{
              "t-1":{"status":"ok"},
              "t-2":{"status":"error","message":"secret","details":{"error":"DeviceNotRegistered"}}
            }}
            """));

        var result = await Client(handler, accessToken: "tok").GetReceiptsAsync(["t-1", "t-2", "t-3"]);

        Assert.True(result.Succeeded);
        Assert.Equal(new ExpoPushReceipt(true, null), result.Receipts["t-1"]);
        Assert.Equal(new ExpoPushReceipt(false, "DeviceNotRegistered"), result.Receipts["t-2"]);
        Assert.False(result.Receipts.ContainsKey("t-3"));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://exp.host/--/api/v2/push/getReceipts", request.Uri);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(["t-1", "t-2", "t-3"], body.RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task GetReceiptsAsync_HttpError_ReturnsTheError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await Client(handler).GetReceiptsAsync(["t-1"]);

        Assert.False(result.Succeeded);
        Assert.Equal("Http502", result.Error);
    }

    private static ExpoPushClient Client(StubHandler handler, string? accessToken = null) =>
        new(Http(handler), Options.Create(new ExpoPushOptions { AccessToken = accessToken }), new CapturingLogger<ExpoPushClient>());

    private static HttpClient Http(StubHandler handler) => new(handler) { BaseAddress = ExpoPushOptions.ApiBaseAddress };

    private static ExpoPushMessage Message(string token) => new()
    {
        To = token,
        Title = "Titolo",
        Body = "Testo",
        ChannelId = PushDeliveryJob.AndroidChannelId,
        Data = new Dictionary<string, string> { ["type"] = "new-booking", ["route"] = "/properties" },
    };

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed record CapturedRequest(string Uri, System.Net.Http.Headers.AuthenticationHeaderValue? Authorization, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization, body));
            return answer(request);
        }
    }
}
