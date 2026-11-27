using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

public class Auth0InvitationLinksTests
{
    private const string PublicBase = "https://casazen-test.example.com";
    private const string Email = "recipient@example.com";
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("owner", "/portale-proprietari/invito?token=casazen-token")]
    [InlineData("owner-contact", "/portale-proprietari/contatto?token=casazen-token")]
    [InlineData("team", "/invite/accept?token=casazen-token")]
    [InlineData("host", "/invito-host/casazen-token")]
    [InlineData("supplier", "/register?inviteToken=casazen-token")]
    public async Task EmailContainsOneLinkAndAuth0DoesNotSendASecondEmail(string flow, string originalPath)
    {
        var original = PublicBase + originalPath;
        using var handler = new Auth0Handler();
        var emailService = new Mock<IEmailService>(MockBehavior.Strict);
        var newHref = EmailHtmlBuilder.Encode(
            PublicBase + "/attiva-invito#organization=org_test&invitation=inv_example&flow=" + flow + "&token=casazen-token");
        var originalHtml = $"<a href=\"{EmailHtmlBuilder.Encode(original)}\">Invito</a>";
        emailService.Setup(service => service.SendEmailAsync(Email, "Invito", $"<a href=\"{newHref}\">Invito</a>"))
            .ReturnsAsync(EmailSendResult.Sent()).Verifiable();

        var job = new EmailDeliveryJob(emailService.Object, NullLogger<EmailDeliveryJob>.Instance, CreateLinks(handler));

        await job.SendAccountInvitationAsync(
            Email, "Invito", originalHtml, "test", new(flow, original, Now.AddDays(7).UtcDateTime), CancellationToken.None);

        emailService.Verify();
        Assert.Equal("/api/v2/organizations/org_test/invitations", handler.RequestPath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(Email, handler.Payload.GetProperty("invitee").GetProperty("email").GetString());
        Assert.Equal("test-spa", handler.Payload.GetProperty("client_id").GetString());
        Assert.Equal("con_test", handler.Payload.GetProperty("connection_id").GetString());
        Assert.Equal(604800, handler.Payload.GetProperty("ttl_sec").GetInt32());
        Assert.False(handler.Payload.GetProperty("send_invitation_email").GetBoolean());
        Assert.False(handler.Payload.TryGetProperty("roles", out _));
    }

    [Fact]
    public async Task ExpiredInvitationDoesNotCreateAnAuth0Ticket()
    {
        using var handler = new Auth0Handler();
        var link = await CreateLinks(handler).CreateAsync(
            Email, new("owner", PublicBase + "/portale-proprietari/invito?token=expired", Now.UtcDateTime), CancellationToken.None);

        Assert.Null(link);
        Assert.Null(handler.RequestPath);
    }

    private static Auth0InvitationLinks CreateLinks(Auth0Handler handler)
    {
        var tokens = new Mock<IAuth0ManagementTokenProvider>();
        tokens.SetupGet(provider => provider.IsConfigured).Returns(true);
        tokens.SetupGet(provider => provider.Domain).Returns("test.auth0.com");
        tokens.Setup(provider => provider.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("management-token");
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(factory => factory.CreateClient(Auth0ManagementTokenProvider.HttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new Auth0InvitationLinks(
            Options.Create(new Auth0InvitationOptions { OrganizationId = "org_test", ClientId = "test-spa", ConnectionId = "con_test" }),
            tokens.Object,
            clients.Object,
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = PublicBase })),
            new FixedTime());
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Auth0Handler : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public JsonElement Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            Payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new
                {
                    invitation_url = PublicBase + "/login?invitation=inv_example&organization=org_test",
                }),
            };
        }
    }
}
