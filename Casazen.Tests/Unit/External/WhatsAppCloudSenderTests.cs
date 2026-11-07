using System.Net;
using System.Text;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.External;

public class WhatsAppCloudSenderTests
{
    [Fact]
    public async Task SendTemplate_AMessageId_IsAccepted()
    {
        var sender = Sender(_ => Json(HttpStatusCode.OK, """{"messages":[{"id":"wamid.1"}]}"""));

        var result = await sender.SendTemplateAsync("+39 333 1234567", "rent_reminder", "it", [new("Giulia")]);

        Assert.True(result.Sent);
        Assert.Equal("wamid.1", result.ProviderMessageId);
    }

    [Fact]
    public async Task SendTemplate_ARejectedStatus_IsNotAccepted()
    {
        var sender = Sender(_ => Json(HttpStatusCode.BadRequest, """{"error":{"message":"bad number"}}"""));

        var result = await sender.SendTemplateAsync("+393331234567", "rent_reminder", "it", [new("Giulia")]);

        Assert.False(result.Sent);
    }

    [Fact]
    public async Task SendTemplate_ASuccessBodyThatIsNotJson_IsNotAccepted()
    {
        var sender = Sender(_ => Json(HttpStatusCode.OK, "not-json"));

        var result = await sender.SendTemplateAsync("+393331234567", "rent_reminder", "it", [new("Giulia")]);

        Assert.False(result.Sent);
    }

    [Fact]
    public async Task SendTemplate_ATimeout_IsNotAccepted()
    {
        var sender = Sender(_ => throw new TaskCanceledException("the client timed out"));

        var result = await sender.SendTemplateAsync("+393331234567", "rent_reminder", "it", [new("Giulia")]);

        Assert.False(result.Sent);
    }

    [Fact]
    public async Task SendTemplate_TheCallerCancelling_Propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var sender = Sender(_ => throw new TaskCanceledException("cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendTemplateAsync("+393331234567", "rent_reminder", "it", [new("Giulia")], cancelled.Token));
    }

    private static WhatsAppCloudSender Sender(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var client = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://graph.facebook.com/") };
        var options = Options.Create(new WhatsAppOptions
        {
            Enabled = true,
            PhoneNumberId = "123",
            AccessToken = "token",
        });
        return new WhatsAppCloudSender(client, options, NullLogger<WhatsAppCloudSender>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
