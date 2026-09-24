using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Hangfire.Common;
using Hangfire.States;
using Casazen.Web.BackgroundJobs;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public class WebhookSignatureTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ESignSecret = "esign-test-secret";
    private const string StripePlatformSecret = "whsec_test_casazen_integration";
    private readonly CasazenWebApplicationFactory _factory;

    public WebhookSignatureTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC6_ESign_ValidHexHmac_Returns200AndEnqueuesJob()
    {
        _factory.BackgroundJobClientMock.Invocations.Clear();
        var payload = """{"externalSessionId":"stub-session-test","allSigned":false}""";
        using var client = _factory.CreateClient();
        using var request = SignedESignRequest(payload, ESignSecret);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j =>
                    j.Type == typeof(ESignWebhookJob)
                    && j.Method.Name == nameof(ESignWebhookJob.ProcessEventAsync)),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [Fact]
    public async Task AC6_ESign_MissingSignature_Returns401()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/esign")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC6_ESign_NonHexSignature_Returns401()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/esign")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-ESign-Signature", "not-hex!!");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC6_ESign_WrongSecret_Returns401()
    {
        var payload = """{"externalSessionId":"stub"}""";
        using var client = _factory.CreateClient();
        using var request = SignedESignRequest(payload, "wrong-secret");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC6_Stripe_InvalidSignature_Returns400()
    {
        _factory.BackgroundJobClientMock.Invocations.Clear();
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new StringContent("""{"id":"evt_test"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", "t=1,v1=deadbeef");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_signature", problem.GetProperty("code").GetString());
        AssertNoStripeJobQueued();
    }

    [Fact]
    public async Task StripeWebhook_SignedWithAnotherSecret_Returns400AndQueuesNothing()
    {
        _factory.BackgroundJobClientMock.Invocations.Clear();
        using var client = _factory.CreateClient();
        using var request = SignedStripeRequest("/webhooks/stripe", StripeEventJson("evt_forged"), "whsec_attacker");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoStripeJobQueued();
    }

    [Fact]
    public async Task StripeWebhook_ValidSignature_Returns200AndQueuesTheEventId()
    {
        _factory.BackgroundJobClientMock.Invocations.Clear();
        var eventId = $"evt_signed_{Guid.NewGuid():N}";
        using var client = _factory.CreateClient();
        using var request = SignedStripeRequest("/webhooks/stripe", StripeEventJson(eventId), StripePlatformSecret);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j =>
                    j.Type == typeof(StripeWebhookJob)
                    && (string)j.Args[0] == eventId
                    && (WebhookSource)j.Args[3] == WebhookSource.Platform),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [Fact]
    public async Task StripeConnectWebhook_SignedWithCommittedPlaceholderSecret_Returns500AndQueuesNothing()
    {
        // appsettings.json ships "whsec_YOUR_CONNECT_SECRET" and the test host does not override it: a secret anyone
        // can read is not a secret, so the endpoint answers "not configured" instead of verifying with it.
        _factory.BackgroundJobClientMock.Invocations.Clear();
        using var client = _factory.CreateClient();
        using var request = SignedStripeRequest("/webhooks/stripe/connect", StripeEventJson("evt_connect"), "whsec_YOUR_CONNECT_SECRET");

        var response = await client.SendAsync(request);

        // Never accepted without a real signature check: Stripe keeps retrying until the secret is set.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stripe_webhook_not_configured", problem.GetProperty("code").GetString());
        AssertNoStripeJobQueued();
    }

    private void AssertNoStripeJobQueued() =>
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(It.Is<Job>(j => j.Type == typeof(StripeWebhookJob)), It.IsAny<IState>()),
            Times.Never);

    private static string StripeEventJson(string eventId) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = Stripe.StripeConfiguration.ApiVersion,
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            pending_webhooks = 1,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            type = "invoice.paid",
            data = new { @object = new { id = "in_signed", @object = "invoice", metadata = new { } } },
        });

    private static HttpRequestMessage SignedStripeRequest(string path, string payload, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={Convert.ToHexString(signature).ToLowerInvariant()}");
        return request;
    }

    private static HttpRequestMessage SignedESignRequest(string payload, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/esign")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        request.Headers.Add("X-ESign-Signature", Convert.ToHexString(hash));
        return request;
    }
}
