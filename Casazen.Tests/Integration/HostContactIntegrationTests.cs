using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>The web host with the emails recorded instead of queued, so a test can read what the host of a site would receive.</summary>
public sealed class HostContactFactory : CasazenWebApplicationFactory
{
    private readonly RecordingEmailQueue _emails = new();

    /// <summary>What the host queued so far: recipient, content and template name.</summary>
    public IReadOnlyList<(string? To, EmailContent Content, string Template)> SentEmails() => _emails.Snapshot();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // The per-IP limit is out of the way (every request of a test shares one partition); the limit per address and org is left at
        // its default, 3 a day, because it is what the second test proves.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["RateLimiting:PublicContact:PermitLimit"] = "1000" }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailQueue>();
            services.AddSingleton<IEmailQueue>(_emails);
        });
    }
}

/// <summary>
/// DB-03b over HTTP: «Scrivi a {host}» of the public booking site. The answer is the same 202 whatever the org publishes or has (so it
/// tells a stranger nothing about the org), the email that goes to the host carries the visitor's address as its reply-to and the text of
/// the visitor encoded, and one address cannot write to one org more than three times a day.
/// </summary>
public class HostContactIntegrationTests(HostContactFactory factory) : IClassFixture<HostContactFactory>
{
    private const string MessageText = "Ciao <b>host</b>, c'è posto a Ferragosto?\nGrazie!";

    [Fact]
    public async Task Contact_IsAnsweredTheSameForEveryOrg_AndOnlyAMessageTheHostCanAnswerIsSent()
    {
        // An org that keeps its contact email private, one that publishes it, one that has none, and a bot that fills the honeypot.
        var keepsPrivate = await SeedOrgAsync("private-host@example.com", publishesContact: false);
        var publishes = await SeedOrgAsync("public-host@example.com", publishesContact: true);
        var noEmail = await SeedOrgAsync(string.Empty, publishesContact: false);
        using var client = factory.CreateClient();

        var first = await PostAsync(client, keepsPrivate, Body("visitor-one@example.com"));
        var second = await PostAsync(client, publishes, Body("visitor-two@example.com"));
        var third = await PostAsync(client, noEmail, Body("visitor-three@example.com"));
        var bot = await PostAsync(client, publishes, Body("visitor-four@example.com", website: "https://spam.example"));

        // The same answer, with nothing in it, for all four.
        foreach (var response in new[] { first, second, third, bot })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        // Only the two messages that have a mailbox to go to were sent, each to its own org, answered to the visitor.
        var sent = factory.SentEmails()
            .Where(e => e.Template == EmailTemplates.Names.HostContactMessage
                && e.Content.ReplyTo is "visitor-one@example.com" or "visitor-two@example.com" or "visitor-three@example.com" or "visitor-four@example.com")
            .ToList();
        Assert.Equal(
            ["private-host@example.com", "public-host@example.com"],
            sent.Select(e => e.To).OrderBy(to => to));
        Assert.Equal(
            ["visitor-one@example.com", "visitor-two@example.com"],
            sent.Select(e => e.Content.ReplyTo).OrderBy(reply => reply));

        // The text arrives encoded (no markup of a stranger in the host's mailbox), line breaks kept, and the subject names nobody.
        var body = sent[0].Content.HtmlBody;
        Assert.Contains("Ciao &lt;b&gt;host&lt;/b&gt;", body);
        Assert.DoesNotContain("<b>host</b>", body);
        Assert.Contains("<br />", body);
        Assert.DoesNotContain("example.com", sent[0].Content.Subject);
    }

    [Fact]
    public async Task Contact_TheFourthMessageOfOneAddressToOneOrg_IsRefused_AndOtherAddressesAndOrgsAreNot()
    {
        var org = await SeedOrgAsync("limit-host@example.com", publishesContact: true);
        var otherOrg = await SeedOrgAsync("limit-other-host@example.com", publishesContact: true);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, org, Body("chatty@example.com"))).StatusCode);

        var refused = await PostAsync(client, org, Body("chatty@example.com"));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("rate_limited", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.True(refused.Headers.Contains("Retry-After"));
        // The limit is for this address at this org: another address, and the same address at another org, can still write.
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, org, Body("quiet@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, otherOrg, Body("chatty@example.com"))).StatusCode);
    }

    private static object Body(string email, string? website = null) => new
    {
        email,
        message = MessageText,
        name = "Anna  Verdi",
        lang = "en",
        consent = true,
        website = website ?? string.Empty,
    };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string slug, object body) =>
        client.PostAsJsonAsync($"/api/public/orgs/{slug}/contact", body);

    private async Task<string> SeedOrgAsync(string contactEmail, bool publishesContact)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slug = $"contact-{Guid.NewGuid():N}";
        db.Orgs.Add(new OrgEntity
        {
            Name = "DB-03b Contact Org",
            Slug = slug,
            DisplayName = "DB-03b Contact Org",
            ContactEmail = contactEmail,
            ContactEmailPublic = publishesContact,
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return slug;
    }
}
