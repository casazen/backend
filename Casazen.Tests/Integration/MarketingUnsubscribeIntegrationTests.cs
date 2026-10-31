using System.Net;
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

    /// <summary>The body a mail client posts for <c>List-Unsubscribe-Post</c> (RFC 8058).</summary>
    private static FormUrlEncodedContent OneClickBody() => new([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]);
}
