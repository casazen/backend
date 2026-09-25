using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SE-01 migration (<see cref="SeoRevisionReview"/>) on real PostgreSQL (A8-06, R-09): the pages published today with the
/// placeholder text of the stub provider, an empty text or no text at all go back to draft, with a "Withdrawn" audit row
/// that documents why; a page published with real text keeps it as its published revision. The rows are inserted with
/// raw SQL on the schema right before the migration.
/// </summary>
public class SeoRevisionReviewMigrationPostgresTests : IAsyncLifetime
{
    private static readonly DateTime Earlier = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_PublishedStubEmptyOrTextlessPages_AreWithdrawnAndAudited_RealTextStaysPublished()
    {
        var stubPage = Guid.NewGuid();
        var stubRevision = Guid.NewGuid();
        var legacyStubPage = Guid.NewGuid();
        var legacyStubRevision = Guid.NewGuid();
        var emptyPage = Guid.NewGuid();
        var emptyRevision = Guid.NewGuid();
        var textlessPage = Guid.NewGuid();
        var realPage = Guid.NewGuid();
        var realOlderRevision = Guid.NewGuid();
        var realLatestRevision = Guid.NewGuid();
        var draftStubPage = Guid.NewGuid();
        var draftStubRevision = Guid.NewGuid();

        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));

            // R-09: the Milano page served "Milano: contenuto generato per affitti brevi, CIN e tassa di soggiorno."
            await InsertPageAsync(db, stubPage, "015146", published: true);
            await InsertRevisionAsync(db, stubRevision, stubPage, "<p>Milano: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p>", Later);
            // The same text stored before the FD-15 sanitizer, with its <article> wrapper.
            await InsertPageAsync(db, legacyStubPage, "013075", published: true);
            await InsertRevisionAsync(db, legacyStubRevision, legacyStubPage, "<article><p>Como: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p></article>", Later);
            await InsertPageAsync(db, emptyPage, "058091", published: true);
            await InsertRevisionAsync(db, emptyRevision, emptyPage, "<article><p> &nbsp;</p></article>", Later);
            await InsertPageAsync(db, textlessPage, "048017", published: true);
            await InsertPageAsync(db, realPage, "010025", published: true);
            await InsertRevisionAsync(db, realOlderRevision, realPage, "<h2>CIN</h2><p>Prima versione</p>", Earlier);
            await InsertRevisionAsync(db, realLatestRevision, realPage, "<h2>CIN</h2><p>Versione servita oggi</p>", Later);
            await InsertPageAsync(db, draftStubPage, "063049", published: false);
            await InsertRevisionAsync(db, draftStubRevision, draftStubPage, "<p>Napoli: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p>", Later);

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var pages = await after.SeoContentPages.AsNoTracking().ToDictionaryAsync(p => p.Id);
        foreach (var withdrawn in new[] { stubPage, legacyStubPage, emptyPage, textlessPage })
        {
            Assert.Equal(LegalReviewStatus.Draft, pages[withdrawn].LegalReviewStatus);
            Assert.Null(pages[withdrawn].PublishedRevisionId);
            Assert.Null(pages[withdrawn].PublishedAt);
        }

        // What the public saw until now stays published: the latest revision of a page with real text.
        Assert.Equal(LegalReviewStatus.Reviewed, pages[realPage].LegalReviewStatus);
        Assert.Equal(realLatestRevision, pages[realPage].PublishedRevisionId);
        Assert.Equal(Earlier, pages[realPage].PublishedAt);
        Assert.Null(pages[draftStubPage].PublishedRevisionId);
        Assert.Equal(LegalReviewStatus.Draft, pages[draftStubPage].LegalReviewStatus);

        var statuses = await after.SeoContentRevisions.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.ContentStatus);
        Assert.Equal(SeoContentStatus.Placeholder, statuses[stubRevision]);
        Assert.Equal(SeoContentStatus.Placeholder, statuses[legacyStubRevision]);
        Assert.Equal(SeoContentStatus.Placeholder, statuses[draftStubRevision]);
        Assert.Equal(SeoContentStatus.EmptyOutput, statuses[emptyRevision]);
        Assert.Equal(SeoContentStatus.Generated, statuses[realOlderRevision]);
        Assert.Equal(SeoContentStatus.Generated, statuses[realLatestRevision]);

        var audit = await after.SeoContentReviewEvents.AsNoTracking().ToListAsync();
        Assert.Equal(4, audit.Count);
        Assert.All(audit, e =>
        {
            Assert.Equal(SeoReviewAction.Withdrawn, e.Action);
            Assert.Equal(SeoRevisionReview.MigrationActor, e.ActorUserId);
            Assert.False(e.CounselApproved);
            Assert.StartsWith("Ritirata dalla migrazione SE-01", e.Note);
        });
        Assert.Equal(
            new Guid?[] { stubRevision, legacyStubRevision, emptyRevision, null }.OrderBy(id => id).ToList(),
            audit.Select(e => e.RevisionId).OrderBy(id => id).ToList());
        Assert.Contains("segnaposto", audit.Single(e => e.PageId == stubPage).Note);
        Assert.Contains("vuoto", audit.Single(e => e.PageId == emptyPage).Note);
        Assert.Contains("senza alcun testo", audit.Single(e => e.PageId == textlessPage).Note);
    }

    private static Task InsertPageAsync(AppDbContext db, Guid id, string comuneCode, bool published)
    {
        DateTime? publishedAt = published ? Earlier : null;
        var status = published ? (int)LegalReviewStatus.Reviewed : (int)LegalReviewStatus.Draft;
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SeoContentPages" ("Id", "Slug", "ComuneCode", "RegionCode", "PageType", "Title", "MetaDescription",
                                           "LegalReviewStatus", "CounselRequired", "PublishedAt", "LastRefreshedAt",
                                           "CreatedAt", "UpdatedAt")
            VALUES ({id}, {"affitti-brevi/" + comuneCode}, {comuneCode}, 'LOM', 0, 'Titolo', 'Meta',
                    {status}, true, {publishedAt}, {Later}, {Earlier}, {Later});
            """);
    }

    private static Task InsertRevisionAsync(AppDbContext db, Guid id, Guid pageId, string bodyHtml, DateTime generatedAt) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SeoContentRevisions" ("Id", "PageId", "BodyHtml", "AiModelTier", "PromptTokens", "GeneratedAt", "SourceDataVersion")
            VALUES ({id}, {pageId}, {bodyHtml}, 0, 120, {generatedAt}, 'v1');
            """);

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_" + nameof(SeoRevisionReview), StringComparison.Ordinal));
        Assert.True(index > 0, "SeoRevisionReview migration not found.");
        return all[index - 1];
    }
}
