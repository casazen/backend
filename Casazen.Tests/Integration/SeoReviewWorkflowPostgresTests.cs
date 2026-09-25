using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SE-01 (A8-04, A8-05, A8-06, A8-21) on real PostgreSQL, through the HTTP API: generation (bootstrap included) only
/// creates drafts, the public site and the sitemap show only the revision an admin approved, every approval and
/// withdrawal is audited, and a text the AI provider did not really generate can never be published.
/// </summary>
/// <remarks>Each test uses its own comuni: the tests of the class share one database.</remarks>
public class SeoReviewWorkflowPostgresTests : IClassFixture<SeoReviewWorkflowFactory>
{
    private const string AdminUserId = "auth0|se01-seo-reviewer";

    private readonly SeoReviewWorkflowFactory _factory;

    public SeoReviewWorkflowPostgresTests(SeoReviewWorkflowFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task GenerationJob_BootstrapAndLegacyAutoApproveJob_CreateDraftsThatAreNeverPublished()
    {
        _factory.Ai.Respond = _ => ValidText("Bellagio e Menaggio");

        // The bootstrap job, and a job queued before SE-01 that still asks for the automatic approval.
        await RunJobAsync(job => job.ExecuteAsync(["013133"]));
        await RunJobAsync(job => job.ExecuteAsync(["013040"], autoApproveCounsel: true));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pages = await db.SeoContentPages.AsNoTracking()
            .Where(p => p.ComuneCode == "013133" || p.ComuneCode == "013040")
            .ToListAsync();
        Assert.Equal(4, pages.Count);
        Assert.All(pages, page =>
        {
            Assert.Equal(LegalReviewStatus.Draft, page.LegalReviewStatus);
            Assert.Null(page.PublishedRevisionId);
            Assert.Null(page.PublishedAt);
        });
        var pageIds = pages.Select(p => p.Id).ToList();
        var revisions = await db.SeoContentRevisions.AsNoTracking().Where(r => pageIds.Contains(r.PageId)).ToListAsync();
        Assert.Equal(4, revisions.Count);
        Assert.All(revisions, r => Assert.Equal(SeoContentStatus.Generated, r.ContentStatus));
        Assert.False(await db.SeoContentReviewEvents.AnyAsync(e => pageIds.Contains(e.PageId)));

        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/content/affitti-brevi/lombardia/bellagio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/content/tassa-soggiorno/menaggio")).StatusCode);
        var locations = await SitemapLocationsAsync(client);
        Assert.DoesNotContain(locations, loc => loc.Contains("bellagio", StringComparison.Ordinal) || loc.Contains("menaggio", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Regeneration_OfApprovedPage_KeepsTheApprovedRevisionPublicUntilTheNewOneIsApproved()
    {
        _factory.Ai.Respond = _ => ValidText("Varenna prima versione");
        await RunJobAsync(job => job.ExecuteAsync(["013182"], [SeoPageType.ComplianceGuide], false));
        var admin = AdminClient();
        var (pageId, firstRevision) = await PageAndLatestRevisionAsync(admin, "013182");
        await ApproveAsync(admin, pageId, firstRevision, HttpStatusCode.OK);

        // Monthly refresh or "Genera" with force: a new text nobody has read yet.
        _factory.Ai.Respond = _ => ValidText("Varenna seconda versione");
        await RunJobAsync(job => job.ExecuteAsync(["013182"], [SeoPageType.ComplianceGuide], true));

        var client = _factory.CreateClient();
        using (var publicPage = JsonDocument.Parse(await client.GetStringAsync("/api/public/content/affitti-brevi/lombardia/varenna")))
        {
            var body = publicPage.RootElement.GetProperty("bodyHtml").GetString();
            Assert.Contains("Varenna prima versione", body);
            Assert.DoesNotContain("seconda versione", body);
        }

        Assert.Contains($"{PublicSite}/p/affitti-brevi/lombardia/varenna", await SitemapLocationsAsync(client));

        using (var detail = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/seo/pages/{pageId}")))
        {
            var page = detail.RootElement.GetProperty("page");
            Assert.Equal("Draft", page.GetProperty("legalReviewStatus").GetString());
            Assert.True(page.GetProperty("isPublished").GetBoolean());
            Assert.True(page.GetProperty("hasPendingRevision").GetBoolean());
            Assert.Equal(firstRevision, detail.RootElement.GetProperty("publishedRevision").GetProperty("id").GetGuid());
            Assert.Contains("Varenna seconda versione", detail.RootElement.GetProperty("pendingRevision").GetProperty("bodyHtml").GetString());
        }

        // The admin who opened the old text cannot approve it over the new one.
        var outdated = await ApproveAsync(admin, pageId, firstRevision, HttpStatusCode.Conflict);
        Assert.Equal(SeoReviewErrorCodes.RevisionOutdated, outdated.GetProperty("code").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(firstRevision, (await db.SeoContentPages.AsNoTracking().SingleAsync(p => p.Id == pageId)).PublishedRevisionId);
    }

    [PostgresFact]
    public async Task Approve_RecordsWhoWhenWhichRevisionAndNote()
    {
        _factory.Ai.Respond = _ => ValidText("Firenze");
        await RunJobAsync(job => job.ExecuteAsync(["048017"], [SeoPageType.ComplianceGuide], false));
        var admin = AdminClient();
        var (pageId, revisionId) = await PageAndLatestRevisionAsync(admin, "048017");

        // One of the first 100 pages: no approval without the explicit legal review confirmation.
        var counsel = await ApproveAsync(admin, pageId, revisionId, HttpStatusCode.UnprocessableEntity, counselApproved: false);
        Assert.Equal(SeoReviewErrorCodes.CounselRequired, counsel.GetProperty("code").GetString());
        await ApproveAsync(admin, pageId, Guid.NewGuid(), HttpStatusCode.NotFound);

        var before = DateTime.UtcNow;
        await ApproveAsync(admin, pageId, revisionId, HttpStatusCode.OK, note: "Revisione legale completata");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.SeoContentReviewEvents.AsNoTracking().SingleAsync(e => e.PageId == pageId);
            Assert.Equal(SeoReviewAction.Approved, audit.Action);
            Assert.Equal(AdminUserId, audit.ActorUserId);
            Assert.Equal(revisionId, audit.RevisionId);
            Assert.True(audit.CounselApproved);
            Assert.Equal("Revisione legale completata", audit.Note);
            Assert.InRange(audit.OccurredAt, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
            var page = await db.SeoContentPages.AsNoTracking().SingleAsync(p => p.Id == pageId);
            Assert.Equal(revisionId, page.PublishedRevisionId);
            Assert.Equal(LegalReviewStatus.Reviewed, page.LegalReviewStatus);
            Assert.Equal(audit.OccurredAt, page.PublishedAt);
        }

        using (var detail = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/seo/pages/{pageId}")))
        {
            var history = Assert.Single(detail.RootElement.GetProperty("reviewHistory").EnumerateArray());
            Assert.Equal("Approved", history.GetProperty("action").GetString());
            Assert.Equal(AdminUserId, history.GetProperty("actorUserId").GetString());
            Assert.Equal(revisionId, history.GetProperty("revisionId").GetGuid());
            Assert.Equal("Revisione legale completata", history.GetProperty("note").GetString());
        }

        var again = await ApproveAsync(admin, pageId, revisionId, HttpStatusCode.Conflict);
        Assert.Equal(SeoReviewErrorCodes.RevisionAlreadyPublished, again.GetProperty("code").GetString());
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/public/content/affitti-brevi/toscana/firenze")).StatusCode);
    }

    [PostgresFact]
    public async Task Withdraw_PublishedPage_LeavesPublicSiteHubAndSitemapAndIsAudited()
    {
        _factory.Ai.Respond = _ => ValidText("Torino");
        await RunJobAsync(job => job.ExecuteAsync(["010025"], [SeoPageType.ComplianceGuide], false));
        var admin = AdminClient();
        var (pageId, revisionId) = await PageAndLatestRevisionAsync(admin, "010025");
        await ApproveAsync(admin, pageId, revisionId, HttpStatusCode.OK);
        var client = _factory.CreateClient();
        const string path = "/p/affitti-brevi/piemonte/torino";
        Assert.Contains(PublicSite + path, await SitemapLocationsAsync(client));

        var response = await admin.PostAsJsonAsync($"/api/admin/seo/pages/{pageId}/withdraw", new { note = "Testo da correggere" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var withdrawn = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.False(withdrawn.RootElement.GetProperty("isPublished").GetBoolean());
            Assert.Equal("Draft", withdrawn.RootElement.GetProperty("legalReviewStatus").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/content/affitti-brevi/piemonte/torino")).StatusCode);
        Assert.DoesNotContain(PublicSite + path, await SitemapLocationsAsync(client));
        using (var hub = JsonDocument.Parse(await client.GetStringAsync("/api/public/content")))
        {
            Assert.DoesNotContain(
                hub.RootElement.GetProperty("pages").EnumerateArray(),
                p => p.GetProperty("path").GetString() == path);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.SeoContentReviewEvents.AsNoTracking()
                .Where(e => e.PageId == pageId)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync();
            Assert.Equal(new[] { SeoReviewAction.Approved, SeoReviewAction.Withdrawn }, audit.Select(e => e.Action));
            Assert.Equal(revisionId, audit[1].RevisionId);
            Assert.Equal(AdminUserId, audit[1].ActorUserId);
            Assert.Equal("Testo da correggere", audit[1].Note);
            var page = await db.SeoContentPages.AsNoTracking().SingleAsync(p => p.Id == pageId);
            Assert.Null(page.PublishedRevisionId);
            Assert.Null(page.PublishedAt);
        }

        var again = await admin.PostAsJsonAsync($"/api/admin/seo/pages/{pageId}/withdraw", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using var problem = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(SeoReviewErrorCodes.PageNotPublished, problem.RootElement.GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task StubProvider_Generation_IsNotGeneratedContentThatCannotBePublished()
    {
        var stub = new StubAiProvider(NullLogger<StubAiProvider>.Instance);
        _factory.Ai.RespondAsync = (prompt, cacheKey) => stub.GenerateAsync(prompt, AiModelTier.Economy, cacheKey);
        await RunJobAsync(job => job.ExecuteAsync(["058091"], [SeoPageType.ComplianceGuide], false));
        var admin = AdminClient();
        var (pageId, revisionId) = await PageAndLatestRevisionAsync(admin, "058091");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var revision = await db.SeoContentRevisions.AsNoTracking().SingleAsync(r => r.Id == revisionId);
            Assert.Equal(SeoContentStatus.AiProviderNotConfigured, revision.ContentStatus);
            Assert.Equal(string.Empty, revision.BodyHtml);
        }

        using (var detail = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/seo/pages/{pageId}")))
        {
            Assert.Equal(
                "AiProviderNotConfigured",
                detail.RootElement.GetProperty("pendingRevision").GetProperty("contentStatus").GetString());
        }

        var refused = await ApproveAsync(admin, pageId, revisionId, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(SeoReviewErrorCodes.RevisionNotPublishable, refused.GetProperty("code").GetString());
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/content/affitti-brevi/lazio/roma")).StatusCode);
        Assert.DoesNotContain(PublicSite + "/p/affitti-brevi/lazio/roma", await SitemapLocationsAsync(client));
    }

    [PostgresFact]
    public async Task ListPages_InvalidPaging_Returns400()
    {
        var admin = AdminClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/seo/pages?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/seo/pages?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/seo/pages?page=3&pageSize=5")).StatusCode);
    }

    private string PublicSite => _factory.PublicSiteBaseUrl;

    private HttpClient AdminClient() => _factory.CreateAuthenticatedClient(AdminUserId, "Admin");

    private async Task RunJobAsync(Func<SeoPageGenerationJob, Task> run)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await run(ActivatorUtilities.CreateInstance<SeoPageGenerationJob>(scope.ServiceProvider));
    }

    private static async Task<(Guid PageId, Guid RevisionId)> PageAndLatestRevisionAsync(HttpClient admin, string comuneCode)
    {
        using var list = JsonDocument.Parse(
            await admin.GetStringAsync($"/api/admin/seo/pages?comuneCode={comuneCode}&pageType=ComplianceGuide"));
        var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        return (item.GetProperty("id").GetGuid(), item.GetProperty("latestRevision").GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> ApproveAsync(
        HttpClient admin,
        Guid pageId,
        Guid revisionId,
        HttpStatusCode expected,
        bool counselApproved = true,
        string? note = null)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/admin/seo/pages/{pageId}/approve",
            new { revisionId, counselApproved, note });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<List<string>> SitemapLocationsAsync(HttpClient client)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var xml = await client.GetStringAsync("/api/public/sitemap.xml");
        return XDocument.Parse(xml).Descendants(ns + "loc").Select(loc => loc.Value).ToList();
    }

    /// <summary>An answer that passes the output checks: sections, paragraphs, enough text.</summary>
    private static AiGenerationResult ValidText(string marker)
    {
        var body = new StringBuilder();
        body.Append("<h2>CIN (Codice Identificativo Nazionale)</h2>");
        for (var i = 0; i < 6; i++)
            body.Append($"<p>{marker}: testo di prova sugli adempimenti degli affitti brevi, paragrafo {i} della guida per gli host.</p>");
        body.Append("<h2>Fonti ufficiali</h2><ul><li><a href=\"https://alloggiatiweb.poliziadistato.it\">Alloggiati Web</a></li></ul>");
        return new AiGenerationResult(body.ToString(), 100, 400, AiModelTier.Economy, FromCache: false);
    }
}

/// <summary>The integration factory with a controllable AI provider (SE-01 tests).</summary>
public sealed class SeoReviewWorkflowFactory : CasazenWebApplicationFactory
{
    public ControllableAiProvider Ai { get; } = new();

    public string PublicSiteBaseUrl =>
        Services.GetRequiredService<IOptions<PublicSiteOptions>>().Value.PublicSiteBaseUrl!.TrimEnd('/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<IAiProvider>(services);
            services.AddSingleton<IAiProvider>(Ai);
        });
    }
}

/// <summary>AI provider whose answer each test sets (the tests of a class run one after the other).</summary>
public sealed class ControllableAiProvider : IAiProvider
{
    public Func<string, AiGenerationResult> Respond
    {
        set => RespondAsync = (prompt, _) => Task.FromResult(value(prompt));
    }

    public Func<string, string, Task<AiGenerationResult>> RespondAsync { get; set; } =
        (_, _) => Task.FromResult(new AiGenerationResult(string.Empty, 0, 0, AiModelTier.Economy, FromCache: false));

    public Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default) =>
        RespondAsync(prompt, cacheKey);
}
