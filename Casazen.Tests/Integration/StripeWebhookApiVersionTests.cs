using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Web.BackgroundJobs;
using Hangfire.Common;
using Hangfire.States;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// A3-39: the Stripe endpoint on the dashboard can be pinned to an API version other than the one this SDK build
/// defaults to (<c>Stripe.StripeConfiguration.ApiVersion</c>, currently <c>2025-12-15.clover</c>). Before the fix,
/// <c>EventUtility.ConstructEvent</c> was called with its default <c>throwOnApiVersionMismatch: true</c>, which threw
/// a <see cref="Stripe.StripeException"/> on any mismatch; the controller's catch turned that into a generic 400
/// "invalid_signature", indistinguishable from a forged event. Every real event from a differently-pinned endpoint
/// was then rejected forever and no booking was ever confirmed. These tests sign a valid payload whose own
/// <c>api_version</c> field is an old, different version and assert the event is still accepted — the HMAC signature
/// is still fully verified (see <c>WebhookSignatureTests</c> for the signature-rejection cases).
/// </summary>
public class StripeWebhookApiVersionTests : IClassFixture<StripeConnectWebhookConfiguredFactory>
{
    private const string PlatformSecret = "whsec_test_casazen_integration";
    private const string OldApiVersion = "2020-08-27";
    private readonly StripeConnectWebhookConfiguredFactory _factory;

    public StripeWebhookApiVersionTests(StripeConnectWebhookConfiguredFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/webhooks/stripe", PlatformSecret, WebhookSource.Platform)]
    [InlineData("/webhooks/stripe/connect", StripeConnectWebhookConfiguredFactory.ConnectWebhookSecret, WebhookSource.Connected)]
    public async Task StripeWebhook_EventApiVersionOlderThanSdkDefault_Returns200AndQueuesTheEventId(
        string path, string secret, WebhookSource expectedSource)
    {
        // Arrange
        _factory.BackgroundJobClientMock.Invocations.Clear();
        var eventId = $"evt_old_version_{Guid.NewGuid():N}";
        using var client = _factory.CreateClient();
        using var request = SignedStripeRequest(path, StripeEventJson(eventId, OldApiVersion), secret);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j =>
                    j.Type == typeof(StripeWebhookJob)
                    && (string)j.Args[0] == eventId
                    && (WebhookSource)j.Args[3] == expectedSource),
                It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    [Fact]
    public async Task StripeWebhook_ForgedSignatureWithOldApiVersion_StillReturns400()
    {
        // A tolerant version check must never substitute for signature verification: a mismatched version alone
        // does not let a forged payload through.
        _factory.BackgroundJobClientMock.Invocations.Clear();
        using var client = _factory.CreateClient();
        using var request = SignedStripeRequest(
            "/webhooks/stripe", StripeEventJson("evt_forged_old_version", OldApiVersion), "whsec_attacker");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(It.Is<Job>(j => j.Type == typeof(StripeWebhookJob)), It.IsAny<IState>()),
            Times.Never);
    }

    private static string StripeEventJson(string eventId, string apiVersion) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            api_version = apiVersion,
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            pending_webhooks = 1,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            type = "invoice.paid",
            data = new { @object = new { id = "in_old_version", @object = "invoice", metadata = new { } } },
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
}
