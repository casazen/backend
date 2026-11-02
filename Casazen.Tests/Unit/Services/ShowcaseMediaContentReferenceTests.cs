using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// How the content of a showcase (SP-13a) and the images of the editor (SP-13b) fit together, on <see cref="SupplierShowcaseContentService"/>
/// and an in-memory database: the content can point only to an image the upload stored, so a name somebody made up, or a file the cleanup
/// removed since, is a 422 and never a broken picture on a public page; and the question "is this file in use" that guards the removal looks in
/// the draft, in the published version and in the one published before.
/// </summary>
public class ShowcaseMediaContentReferenceTests
{
    private const string PublicBase = "https://storage.test/public/";

    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Save_AnImageOfTheShowcaseFolderThatTheUploadNeverStored_IsRefused()
    {
        var madeUp = $"{PublicBase}suppliers/{OrgA}/showcase/cover-{Guid.NewGuid():N}.jpg";

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().SaveDraftAsync(OrgA, 0, Patch(CoverJson(madeUp))));

        Assert.Equal(ShowcaseContentErrors.Invalid, refused.Code);
        Assert.Contains("cover.url", refused.Extensions["fields"] as IReadOnlyList<string> ?? []);
    }

    [Fact]
    public async Task Save_AnImageThatTheUploadStored_IsAccepted_AndSoIsOneOfTheProfileFolder()
    {
        var stored = await StoreAsync(OrgA, "cover");
        var profile = $"{PublicBase}suppliers/{OrgA}/photos/team.webp";

        var view = await Service().SaveDraftAsync(OrgA, 0, Patch("{\"cover\":{\"url\":\"" + stored.Url + "\"},\"gallery\":[{\"photoUrl\":\"" + profile + "\"}]}"));

        Assert.Equal(stored.Url, view.Draft.Cover!.Url);
        Assert.Equal(profile, view.Draft.Gallery[0].PhotoUrl);
    }

    [Fact]
    public async Task Save_AnImageThatTheCleanupRemovedMeanwhile_IsRefused_NotSavedAsABrokenPicture()
    {
        var stored = await StoreAsync(OrgA, "work");
        await using (var db = CreateDb())
        {
            db.SupplierShowcaseMedia.RemoveRange(await db.SupplierShowcaseMedia.ToListAsync());
            await db.SaveChangesAsync();
        }

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().SaveDraftAsync(OrgA, 0, Patch(GalleryJson(stored.Url))));

        Assert.Equal(ShowcaseContentErrors.Invalid, refused.Code);
    }

    [Fact]
    public async Task Save_AnImageOfAnotherSuppliersShowcase_IsRefusedEvenWhenItExists()
    {
        var other = await StoreAsync(OrgB, "cover");

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().SaveDraftAsync(OrgA, 0, Patch(CoverJson(other.Url))));

        Assert.Equal(ShowcaseContentErrors.Invalid, refused.Code);
    }

    [Fact]
    public async Task FindKeysInUse_LooksInTheDraftThePublishedVersionAndTheOneBefore_AndOnlyInTheSuppliersOwn()
    {
        var inDraft = await StoreAsync(OrgA, "work");
        var inPublished = await StoreAsync(OrgA, "work");
        var inPrevious = await StoreAsync(OrgA, "work");
        var unused = await StoreAsync(OrgA, "work");

        await using (var db = CreateDb())
        {
            db.SupplierShowcaseContents.Add(new SupplierShowcaseContent
            {
                OrgId = OrgA,
                DraftJson = GalleryJson(inDraft.Url),
                PublishedJson = CoverJson(inPublished.Url),
                PreviousPublishedJson = "{\"logo\":{\"mode\":\"image\",\"url\":\"" + inPrevious.Url + "\"}}",
            });
            await db.SaveChangesAsync();
        }

        var keys = new[] { inDraft, inPublished, inPrevious, unused }.Select(item => KeyOf(item.Url)).ToList();

        var used = await Service().FindKeysInUseAsync(OrgA, keys);
        var usedByTheOtherSupplier = await Service().FindKeysInUseAsync(OrgB, keys);

        Assert.Equal(keys.Take(3).Order(), used.Order());
        Assert.Empty(usedByTheOtherSupplier);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static JsonObject Patch(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string CoverJson(string url) => "{\"cover\":{\"url\":\"" + url + "\"}}";

    private static string GalleryJson(string url) => "{\"gallery\":[{\"photoUrl\":\"" + url + "\"}]}";

    private static string KeyOf(string url) => url[PublicBase.Length..];

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>The row the upload leaves for an image (the content service reads the keys of these rows), and the address of the file.</summary>
    private async Task<(Guid Id, string Url)> StoreAsync(Guid orgId, string kind)
    {
        var key = $"suppliers/{orgId}/showcase/{kind}-{Guid.NewGuid():N}.jpg";
        await using var db = CreateDb();
        var row = new SupplierShowcaseMedia
        {
            OrgId = orgId,
            Kind = kind,
            StorageKey = key,
            ContentType = "image/jpeg",
            Bytes = 100,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        db.SupplierShowcaseMedia.Add(row);
        await db.SaveChangesAsync();
        return (row.Id, PublicBase + key);
    }

    /// <summary>The content service on its own context, as every request gets its own; no service of the catalog is active.</summary>
    private SupplierShowcaseContentService Service()
    {
        var catalog = new Mock<ISupplierServiceCatalogService>();
        catalog
            .Setup(c => c.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SupplierServiceListing>());

        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.TryGetPublicKey(It.IsAny<string>()))
            .Returns((string url) => url.StartsWith(PublicBase, StringComparison.Ordinal) ? url[PublicBase.Length..] : null);

        var db = CreateDb();
        var agenda = new SupplierAgendaService(
            db, new SupplierServiceRequestReader(db), new ShowcaseHoldReader(db), NullLogger<SupplierAgendaService>.Instance, _clock);
        return new SupplierShowcaseContentService(
            db, catalog.Object, agenda, storage.Object, Mock.Of<IFeatureFlags>(), NullLogger<SupplierShowcaseContentService>.Instance, _clock);
    }
}
