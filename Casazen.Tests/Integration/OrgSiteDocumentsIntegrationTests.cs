using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// <c>/api/orgs/me/site-documents</c> and <c>/api/public/orgs/{slug}/documents/{kind}</c> (BK-14, A3-21): the org's
/// administrator publishes the operator's privacy notice and terms as versions, and the anonymous public endpoint serves
/// what is published, or says plainly that nothing is.
/// </summary>
public class OrgSiteDocumentsIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Url = "/api/orgs/me/site-documents";

    private readonly CasazenWebApplicationFactory _factory;

    public OrgSiteDocumentsIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetAll_NewOrg_ReturnsBothKindsNotPublished()
    {
        var ownerId = NewOwnerId("get");
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var json = await client.GetFromJsonAsync<JsonElement>(Url);

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal(["privacy", "terms"], json.EnumerateArray().Select(d => d.GetProperty("kind").GetString()));
        Assert.All(json.EnumerateArray(), d =>
        {
            Assert.False(d.GetProperty("published").GetBoolean());
            Assert.Equal(JsonValueKind.Null, d.GetProperty("current").ValueKind);
            Assert.Equal(0, d.GetProperty("history").GetArrayLength());
        });
    }

    [Fact]
    public async Task HostEndpoints_Anonymous_Return401()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"{Url}/privacy", new { source = "Text", content = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Url}/privacy")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "/privacy")]
    [InlineData("DELETE", "/privacy")]
    [InlineData("GET", "/privacy/versions/1")]
    public async Task HostEndpoints_AsStaffRole_Return403(string method, string suffix)
    {
        var userId = NewOwnerId("staff");
        await _factory.SeedOrgForOwnerAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        using var request = new HttpRequestMessage(new HttpMethod(method), Url + suffix)
        {
            Content = method == "PUT" ? JsonContent.Create(new { source = "Text", content = "x" }) : null,
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Put_Text_IsServedOnThePublicEndpointAsSanitizedHtml()
    {
        var ownerId = NewOwnerId("put");
        var slug = await SeedOrgWithSlugAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"{Url}/privacy", new
        {
            source = "Text",
            content = "# Titolare\nVilla Parco di Mario Rossi\n\n- nome\n- email\n\nScrivi a [privacy](mailto:privacy@villaparco.test)",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(state.GetProperty("published").GetBoolean());
        Assert.Equal(1, state.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Contains("# Titolare", state.GetProperty("current").GetProperty("content").GetString());

        using var anonymous = _factory.CreateClient();
        var publicDocument = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}/documents/privacy");
        Assert.True(publicDocument.GetProperty("published").GetBoolean());
        Assert.Equal("privacy", publicDocument.GetProperty("kind").GetString());
        Assert.Equal(1, publicDocument.GetProperty("version").GetInt32());
        Assert.Equal("Text", publicDocument.GetProperty("source").GetString());
        var html = publicDocument.GetProperty("contentHtml").GetString()!;
        Assert.Contains("<h2>Titolare</h2>", html);
        Assert.Contains("<li>nome</li>", html);
        Assert.Contains("href=\"mailto:privacy@villaparco.test\"", html);
        Assert.Equal(JsonValueKind.Null, publicDocument.GetProperty("externalUrl").ValueKind);
        // The public read never carries the source text, the author or the internal ids.
        Assert.False(publicDocument.TryGetProperty("content", out _));
        Assert.False(publicDocument.TryGetProperty("publishedByUserId", out _));
        Assert.False(publicDocument.TryGetProperty("orgId", out _));
    }

    [Fact]
    public async Task PublicEndpoint_NothingPublished_AnswersNotPublishedNotAnError()
    {
        var ownerId = NewOwnerId("none");
        var slug = await SeedOrgWithSlugAsync(ownerId);

        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/public/orgs/{slug}/documents/terms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("published").GetBoolean());
        Assert.Equal("terms", json.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("contentHtml").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("externalUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("version").ValueKind);
    }

    [Fact]
    public async Task Put_ExternalUrl_IsServedAsAnAddressWithoutText()
    {
        var ownerId = NewOwnerId("url");
        var slug = await SeedOrgWithSlugAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"{Url}/terms", new { source = "ExternalUrl", externalUrl = "https://villaparco.test/termini.pdf" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var anonymous = _factory.CreateClient();
        var json = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}/documents/terms");
        Assert.True(json.GetProperty("published").GetBoolean());
        Assert.Equal("ExternalUrl", json.GetProperty("source").GetString());
        Assert.Equal("https://villaparco.test/termini.pdf", json.GetProperty("externalUrl").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("contentHtml").ValueKind);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "org_document_html_not_allowed")]
    [InlineData("Testo <b>grassetto</b>", "org_document_html_not_allowed")]
    [InlineData("[x](javascript:alert(1))", "org_document_link_invalid")]
    [InlineData("   ", "org_document_content_required")]
    public async Task Put_InvalidText_Returns422AndPublishesNothing(string content, string expectedCode)
    {
        var ownerId = NewOwnerId("bad-text");
        var slug = await SeedOrgWithSlugAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"{Url}/privacy", new { source = "Text", content });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("detail").GetString()));

        using var anonymous = _factory.CreateClient();
        var publicDocument = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}/documents/privacy");
        Assert.False(publicDocument.GetProperty("published").GetBoolean());
    }

    [Theory]
    [InlineData("http://villaparco.test/privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@villaparco.test/privacy")]
    public async Task Put_InvalidAddress_Returns422(string externalUrl)
    {
        var ownerId = NewOwnerId("bad-url");
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"{Url}/privacy", new { source = "ExternalUrl", externalUrl });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_document_url_invalid", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_TextOverTheLimit_Returns422WithTheLimit()
    {
        var ownerId = NewOwnerId("long");
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"{Url}/privacy", new
        {
            source = "Text",
            content = new string('a', OrgSiteDocumentRules.ContentMaxLength + 1),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_document_content_too_long", json.GetProperty("code").GetString());
        Assert.Contains(OrgSiteDocumentRules.ContentMaxLength.ToString(), json.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_TextFarOverTheLimit_Returns400ByTheInputBound()
    {
        var ownerId = NewOwnerId("huge");
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"{Url}/privacy", new
        {
            source = "Text",
            content = new string('a', OrgSiteDocumentRules.ContentMaxLength * 2 + 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"content\":\"x\"}")]
    [InlineData("{\"source\":\"Markdown\",\"content\":\"x\"}")]
    public async Task Put_MissingOrUnknownSource_Returns400(string body)
    {
        var ownerId = NewOwnerId("source");
        await _factory.SeedOrgForOwnerAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PutAsync($"{Url}/privacy", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_SecondText_AddsVersionTwoAndKeepsVersionOneReadable()
    {
        var ownerId = NewOwnerId("versions");
        var slug = await SeedOrgWithSlugAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        await client.PutAsJsonAsync($"{Url}/privacy", new { source = "Text", content = "Prima versione" });
        var second = await client.PutAsJsonAsync($"{Url}/privacy", new { source = "Text", content = "Seconda versione" });

        var state = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, state.GetProperty("current").GetProperty("version").GetInt32());
        var history = state.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal([2, 1], history.Select(h => h.GetProperty("version").GetInt32()));
        // The history list never carries the text.
        Assert.All(history, h => Assert.Equal(JsonValueKind.Null, h.GetProperty("content").ValueKind));

        var first = await client.GetFromJsonAsync<JsonElement>($"{Url}/privacy/versions/1");
        Assert.Equal("Prima versione", first.GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/privacy/versions/3")).StatusCode);

        using var anonymous = _factory.CreateClient();
        var publicDocument = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}/documents/privacy");
        Assert.Equal(2, publicDocument.GetProperty("version").GetInt32());
        Assert.Contains("Seconda versione", publicDocument.GetProperty("contentHtml").GetString());
    }

    [Fact]
    public async Task Delete_PublishedDocument_StopsShowingItAndKeepsTheHistory()
    {
        var ownerId = NewOwnerId("withdraw");
        var slug = await SeedOrgWithSlugAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        await client.PutAsJsonAsync($"{Url}/terms", new { source = "Text", content = "Termini" });
        var response = await client.DeleteAsync($"{Url}/terms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(state.GetProperty("published").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, state.GetProperty("current").GetProperty("withdrawnAt").ValueKind);
        Assert.Equal(1, state.GetProperty("history").GetArrayLength());

        using var anonymous = _factory.CreateClient();
        var publicDocument = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}/documents/terms");
        Assert.False(publicDocument.GetProperty("published").GetBoolean());

        // Withdrawing again is harmless; publishing again adds a new version.
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{Url}/terms")).StatusCode);
        var again = await client.PutAsJsonAsync($"{Url}/terms", new { source = "Text", content = "Termini" });
        var againState = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(againState.GetProperty("published").GetBoolean());
        Assert.Equal(2, againState.GetProperty("current").GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("dpa")]
    [InlineData("termini")]
    public async Task UnknownKind_Returns404OnEveryEndpoint(string kind)
    {
        var ownerId = NewOwnerId("kind");
        var slug = await SeedOrgWithSlugAsync(ownerId);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Url}/{kind}", new { source = "Text", content = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{kind}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{kind}/versions/1")).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/public/orgs/{slug}/documents/{kind}")).StatusCode);
    }

    [Fact]
    public async Task PublicEndpoint_UnknownOrg_Returns404()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/public/orgs/no-such-org-{Guid.NewGuid():N}/documents/privacy");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicEndpoint_PreviousSlugOfTheOrg_ServesTheSameDocument()
    {
        var ownerId = NewOwnerId("alias");
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var oldSlug = $"vecchio-{Guid.NewGuid():N}"[..24];
        var newSlug = $"nuovo-{Guid.NewGuid():N}"[..24];
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            stored.Slug = newSlug;
            db.OrgSlugAliases.Add(new OrgSlugAlias { Slug = oldSlug, OrgId = org.Id, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        await client.PutAsJsonAsync($"{Url}/privacy", new { source = "Text", content = "Informativa" });

        using var anonymous = _factory.CreateClient();
        foreach (var slug in new[] { newSlug, oldSlug })
        {
            var json = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slug}/documents/privacy");
            Assert.True(json.GetProperty("published").GetBoolean(), slug);
        }
    }

    [Fact]
    public async Task HostOfAnotherOrg_ManagesOnlyTheirOwnDocuments()
    {
        var ownerA = NewOwnerId("iso-a");
        var ownerB = NewOwnerId("iso-b");
        var slugA = await SeedOrgWithSlugAsync(ownerA);
        await SeedOrgWithSlugAsync(ownerB);

        using var clientA = _factory.CreateAuthenticatedClient(ownerA, "PropertyOwner");
        using var clientB = _factory.CreateAuthenticatedClient(ownerB, "PropertyOwner");
        await clientA.PutAsJsonAsync($"{Url}/privacy", new { source = "Text", content = "Informativa di A" });

        var stateB = await clientB.GetFromJsonAsync<JsonElement>(Url);
        Assert.All(stateB.EnumerateArray(), d => Assert.False(d.GetProperty("published").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/privacy/versions/1")).StatusCode);

        // B withdrawing "their" privacy does not touch A's.
        var withdraw = await clientB.DeleteAsync($"{Url}/privacy");
        Assert.Equal(HttpStatusCode.OK, withdraw.StatusCode);
        var publicA = await clientB.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slugA}/documents/privacy");
        Assert.True(publicA.GetProperty("published").GetBoolean());
        Assert.Contains("Informativa di A", publicA.GetProperty("contentHtml").GetString());
    }

    [Fact]
    public async Task PublicEndpoint_SignedInHostOfAnotherOrg_SeesTheDocumentOfTheOrgTheyVisit()
    {
        var ownerA = NewOwnerId("visit-a");
        var ownerB = NewOwnerId("visit-b");
        var slugA = await SeedOrgWithSlugAsync(ownerA);
        await SeedOrgWithSlugAsync(ownerB);
        using (var clientA = _factory.CreateAuthenticatedClient(ownerA, "PropertyOwner"))
            await clientA.PutAsJsonAsync($"{Url}/terms", new { source = "Text", content = "Termini di A" });

        // The tenant filter of the signed-in caller (org B) must not hide the public document of org A.
        using var clientB = _factory.CreateAuthenticatedClient(ownerB, "PropertyOwner");
        var json = await clientB.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{slugA}/documents/terms");

        Assert.True(json.GetProperty("published").GetBoolean());
        Assert.Contains("Termini di A", json.GetProperty("contentHtml").GetString());
    }

    [PostgresFact]
    public async Task PublishAsync_ParallelPublishes_GetDistinctConsecutiveVersions()
    {
        var org = await _factory.SeedOrgForOwnerAsync(NewOwnerId("parallel"));

        var tasks = Enumerable.Range(1, 3).Select(async i =>
        {
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IOrgSiteDocumentService>();
            return await service.PublishAsync(
                org.Id,
                OrgSiteDocumentKind.Privacy,
                new OrgSiteDocumentInput(OrgSiteDocumentSource.Text, $"Testo {i}", null),
                null);
        });
        var published = await Task.WhenAll(tasks);

        Assert.Equal([1, 2, 3], published.Select(d => d.Version).Order());
    }

    private static string NewOwnerId(string label) => $"auth0|site-docs-{label}-{Guid.NewGuid():N}";

    /// <summary>An org of the owner with a unique readable slug (the public endpoint resolves orgs by slug).</summary>
    private async Task<string> SeedOrgWithSlugAsync(string ownerId)
    {
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var slug = $"villa-docs-{Guid.NewGuid():N}"[..28];
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        stored.Slug = slug;
        await db.SaveChangesAsync();
        return slug;
    }
}
