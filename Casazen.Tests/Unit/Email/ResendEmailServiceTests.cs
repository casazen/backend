using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Web.Extensions;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Resend;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>FD-13 (A9-06): the configured sender is used as is, never replaced by onboarding@resend.dev.</summary>
public class ResendEmailServiceTests
{
    [Fact]
    public async Task SendEmailAsync_SenderOnCasazenDomain_IsSentUnchanged()
    {
        EmailMessage? sent = null;
        var resend = new Mock<IResend>();
        resend
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => sent = message)
            .ReturnsAsync(new ResendResponse<Guid>(Guid.NewGuid(), null));
        var service = new ResendEmailService(
            resend.Object, EmailTestHelpers.ConfiguredEmail("noreply@casazen.app"), NullLogger<ResendEmailService>.Instance);

        var result = await service.SendEmailAsync("guest@example.com", "Oggetto", "<p>Ciao</p>");

        Assert.True(result.Success);
        Assert.NotNull(sent);
        Assert.Equal("noreply@casazen.app", sent.From.Email);
        Assert.Equal("CasaZen", sent.From.DisplayName);
        Assert.DoesNotContain("resend.dev", sent.From.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PROOF TEST (E50/F01/US03/T01, NF-155: what a guest reads as the sender): an e-mail to a guest is sent in the name of the host, and only that: the display name is the host's,
    /// cleaned (no quote, no bracket, no control character, no separator of an address, at most 64 characters; a name that is nothing after the cleaning leaves the configured one),
    /// and the <b>address</b> of the sender is the configured one whatever the name says.
    /// </summary>
    [Fact]
    public void BuildMessage_FromName_IsSanitizedAndAddressUnchanged()
    {
        var settings = new EmailOptions { ApiKey = "re_test_key", FromAddress = "noreply@casazen.app", FromName = "CasaZen" };
        var hostile = "  \"Casa <Aurora>\"\r\nBcc: evil@x.test [VIP] " + new string('A', 100);

        var message = ResendEmailService.BuildMessage(settings, "guest@example.com", "Oggetto", "<p>Ciao</p>", "host@aurora.test", headers: null, fromName: hostile);

        Assert.Equal("noreply@casazen.app", message.From.Email);
        var name = message.From.DisplayName!;
        Assert.StartsWith("Casa Aurora Bcc evilx.test VIP ", name, StringComparison.Ordinal);
        Assert.Equal(64, name.Length);
        Assert.All(name, c => Assert.False(char.IsControl(c), "a control character in the display name"));
        Assert.DoesNotContain("\"", name, StringComparison.Ordinal);
        Assert.DoesNotContain("<", name, StringComparison.Ordinal);
        Assert.DoesNotContain(">", name, StringComparison.Ordinal);
        Assert.DoesNotContain("[", name, StringComparison.Ordinal);
        Assert.DoesNotContain("@", name, StringComparison.Ordinal);

        // A name that is nothing once cleaned, or none at all, leaves the configured name; the address is the same in every case.
        var nothing = ResendEmailService.BuildMessage(settings, "guest@example.com", "Oggetto", "<p>Ciao</p>", fromName: "<>[]\r\n");
        var none = ResendEmailService.BuildMessage(settings, "guest@example.com", "Oggetto", "<p>Ciao</p>");
        Assert.Equal("CasaZen", nothing.From.DisplayName);
        Assert.Equal("CasaZen", none.From.DisplayName);
        Assert.Equal("noreply@casazen.app", nothing.From.Email);
        Assert.Equal("noreply@casazen.app", none.From.Email);
    }

    [Fact]
    public async Task SendEmailAsync_InTheHostsName_HandsTheProviderTheHostNameOnTheConfiguredAddress()
    {
        EmailMessage? sent = null;
        var resend = new Mock<IResend>();
        resend
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((message, _) => sent = message)
            .ReturnsAsync(new ResendResponse<Guid>(Guid.NewGuid(), null));
        var service = new ResendEmailService(
            resend.Object, EmailTestHelpers.ConfiguredEmail("noreply@casazen.app"), NullLogger<ResendEmailService>.Instance);

        var result = await service.SendEmailAsync("guest@example.com", "Oggetto", "<p>Ciao</p>", "host@aurora.test", "Casa Aurora");

        Assert.True(result.Success);
        Assert.NotNull(sent);
        Assert.Equal("noreply@casazen.app", sent.From.Email);
        Assert.Equal("Casa Aurora", sent.From.DisplayName);
    }

    [Fact]
    public async Task SendEmailAsync_ThroughHttpApi_PostsConfiguredSenderAndApiKey()
    {
        var handler = new CapturingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddCasazenEmail(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:ApiKey"] = "re_test_key",
                ["Email:FromAddress"] = "noreply@casazen.app",
                ["Email:FromName"] = "CasaZen",
                ["App:PublicSiteBaseUrl"] = "https://app.example.org",
            }).Build(),
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Production));
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IEmailService>()
            .SendEmailAsync("guest@example.com", "Oggetto", "<p>Ciao</p>");

        Assert.True(result.Success, result.ErrorDetail);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https", request.Uri.Scheme);
        Assert.EndsWith("/emails", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Bearer re_test_key", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var from = body.RootElement.GetProperty("from").GetString();
        Assert.Contains("noreply@casazen.app", from);
        Assert.DoesNotContain("resend.dev", request.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>DEB-01: the one-click unsubscribe headers of a campaign reach the provider as headers of the e-mail.</summary>
    [Fact]
    public async Task SendEmailWithHeadersAsync_ThroughHttpApi_PostsTheListUnsubscribeHeaders()
    {
        var handler = new CapturingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddCasazenEmail(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:ApiKey"] = "re_test_key",
                ["Email:FromAddress"] = "noreply@casazen.app",
                ["App:PublicSiteBaseUrl"] = "https://app.example.org",
            }).Build(),
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Production));
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        const string oneClick = "<https://api.example.org/api/public/marketing/unsubscribe?orgId=1&emailHash=2&expiresAt=3&signature=4>";

        var result = await scope.ServiceProvider.GetRequiredService<IEmailService>().SendEmailWithHeadersAsync(
            "guest@example.com",
            "Offerta",
            "<p>Ciao</p>",
            new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = oneClick,
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
            });

        Assert.True(result.Success, result.ErrorDetail);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var headers = body.RootElement.GetProperty("headers");
        Assert.Equal(oneClick, headers.GetProperty("List-Unsubscribe").GetString());
        Assert.Equal("List-Unsubscribe=One-Click", headers.GetProperty("List-Unsubscribe-Post").GetString());
    }

    [Fact]
    public async Task SendEmailAsync_NotConfigured_SkipsWithoutCallingProvider()
    {
        var resend = new Mock<IResend>(MockBehavior.Strict);
        var options = Microsoft.Extensions.Options.Options.Create(new EmailOptions { ApiKey = "", FromAddress = "" });
        var service = new ResendEmailService(resend.Object, options, NullLogger<ResendEmailService>.Instance);

        var result = await service.SendEmailAsync("guest@example.com", "Oggetto", "<p>Ciao</p>");

        Assert.False(result.Success);
        Assert.True(result.Skipped);
        Assert.Equal(EmailSendResult.NotConfiguredDetail, result.ErrorDetail);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorType.RateLimitExceeded)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorType.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden, ErrorType.InvalidFromAddress)]
    public async Task SendEmailAsync_ProviderError_ReturnsFailureWithTransientFlagFromProvider(
        HttpStatusCode status,
        ErrorType errorType)
    {
        var exception = new ResendException(status, errorType, "provider error", null);
        var resend = new Mock<IResend>();
        resend
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResendResponse<Guid>(exception, null));
        var service = new ResendEmailService(
            resend.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<ResendEmailService>.Instance);

        var result = await service.SendEmailAsync("guest@example.com", "Oggetto", "<p>Ciao</p>");

        Assert.False(result.Success);
        Assert.False(result.Skipped);
        Assert.Equal(exception.IsTransient, result.IsTransient);
    }

    [Fact]
    public async Task SendEmailAsync_NetworkFailure_ReturnsTransientFailure()
    {
        var resend = new Mock<IResend>();
        resend
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var service = new ResendEmailService(
            resend.Object, EmailTestHelpers.ConfiguredEmail(), NullLogger<ResendEmailService>.Instance);

        var result = await service.SendEmailAsync("guest@example.com", "Oggetto", "<p>Ciao</p>");

        Assert.False(result.Success);
        Assert.True(result.IsTransient);
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"id\":\"{Guid.NewGuid()}\"}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
