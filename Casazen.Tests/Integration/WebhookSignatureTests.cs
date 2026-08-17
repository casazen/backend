using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hangfire.Common;
using Hangfire.States;
using Casazen.Web.BackgroundJobs;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public class WebhookSignatureTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ESignSecret = "esign-test-secret";
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
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new StringContent("""{"id":"evt_test"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", "t=1,v1=deadbeef");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
