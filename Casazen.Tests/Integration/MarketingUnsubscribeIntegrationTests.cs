using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DEB-01 over the real pipeline: the one-click address a campaign puts in <c>List-Unsubscribe</c> is the signed link of ADR-007-R18,
/// and <c>POST api/public/marketing/unsubscribe</c> accepts it as a mail client sends it (RFC 8058: <c>List-Unsubscribe=One-Click</c> in a
/// form body): the address is suppressed for the org of the link, while a link whose signature was touched is refused.
/// </summary>
public class MarketingUnsubscribeIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string LinkSecret = "it-signed-link-secret-0123456789abcdef";

    [Fact]
    public async Task OneClickPost_OfTheCampaignLink_SuppressesTheAddress_ATamperedLinkIsRefused()
    {
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [MarketingUnsubscribeLinks.SecretVariable] = LinkSecret })));
        var org = await factory.SeedOrgForOwnerAsync($"auth0|deb01-unsubscribe-{Guid.NewGuid():N}");
        var email = $"ospite.{Guid.NewGuid():N}@example.com";
        var link = host.Services.GetRequiredService<MarketingUnsubscribeLinks>().Create(org.Id, email);
        var oneClick = new Uri(link.OneClickUrl!).PathAndQuery;
        using var client = host.CreateClient();

        Assert.DoesNotContain("example.com", link.OneClickUrl!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", link.PageUrl, StringComparison.OrdinalIgnoreCase);

        var tampered = await client.PostAsync(oneClick.Replace("signature=", "signature=0"), OneClickBody());
        await OrgTeamHttp.AssertProblemAsync(tampered, HttpStatusCode.UnprocessableEntity, "invalid_signature");
        using (var scope = host.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<ISuppressionService>().IsSuppressedAsync(org.Id, email));

        var response = await client.PostAsync(oneClick, OneClickBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var verify = host.Services.CreateScope();
        Assert.True(await verify.ServiceProvider.GetRequiredService<ISuppressionService>().IsSuppressedAsync(org.Id, email));
    }

    [PostgresFact]
    public async Task SignedResendEventsCountOneDeliveryAcrossRetries()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:MarketingWebhookSecret"] = "whsec-" + Convert.ToBase64String(key) })));
        var org = await factory.SeedOrgForOwnerAsync($"auth0|marketing-metrics-{Guid.NewGuid():N}");
        var delivery = new CampaignDelivery { OrgId = org.Id, CampaignId = Guid.NewGuid(), ProfileId = Guid.NewGuid(),
            ContactId = Guid.NewGuid(), Month = 202611, Slot = 1, CreatedAt = DateTime.UtcNow, Status = "Sent" };
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CampaignDeliveries.Add(delivery);
            await db.SaveChangesAsync();
        }
        using var client = host.CreateClient();
        async Task<HttpStatusCode> Post(string eventType, bool valid)
        {
            var payload = JsonSerializer.Serialize(new { type = eventType, data = new { tags = new { campaign_delivery = delivery.Id.ToString("N") } } });
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            const string message = "msg_test";
            var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{message}.{timestamp}.{payload}"));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/marketing/resend");
            request.Headers.Add("svix-id", message);
            request.Headers.Add("svix-timestamp", timestamp);
            request.Headers.Add("svix-signature", "v1," + Convert.ToBase64String(valid ? signature : new byte[32]));
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Post("email.opened", false));
        foreach (var type in new[] { "email.opened", "email.clicked", "email.bounced", "email.opened", "email.bounced" })
            Assert.Equal(HttpStatusCode.NoContent, await Post(type, true));
        await using var verify = host.Services.CreateAsyncScope();
        var saved = await verify.ServiceProvider.GetRequiredService<AppDbContext>().CampaignDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        Assert.True(saved.Opened && saved.Clicked && saved.Bounced);
    }

    /// <summary>The body a mail client posts for <c>List-Unsubscribe-Post</c> (RFC 8058).</summary>
    private static FormUrlEncodedContent OneClickBody() => new([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]);
}
