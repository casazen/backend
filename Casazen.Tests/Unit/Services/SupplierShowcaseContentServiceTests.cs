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
/// <see cref="SupplierShowcaseContentService"/> (SP-13a) on an in-memory database: the draft of a supplier is its own, a save carries the
/// version it read, and nothing a supplier writes is published by saving. What needs PostgreSQL (the lock, <c>xmin</c>, parallel saves)
/// is a matter of the CI's PostgreSQL run.
/// </summary>
public class SupplierShowcaseContentServiceTests
{
    private const string PublicBase = "https://storage.test/public/";

    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Get_ASupplierThatNeverSaved_GetsAnEmptyDraftAtVersionZero_AndNoRowIsCreated()
    {
        var view = await Service().GetAsync(OrgA);

        Assert.Equal(0u, view.Version);
        Assert.Equal(ShowcaseContentState.Draft, view.State);
        Assert.Null(view.Published);
        Assert.Null(view.Draft.Tagline);
        Assert.Empty(view.Changes);

        await using var db = CreateDb();
        Assert.Empty(await db.SupplierShowcaseContents.ToListAsync());
    }

    [Fact]
    public async Task Save_TheFirstSave_CreatesTheDraft_AndPublishesNothing()
    {
        var view = await Service().SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Pronti in un'ora","color":"#0b5fa5"}"""));

        Assert.Equal(ShowcaseContentState.Draft, view.State);
        Assert.Equal("Pronti in un'ora", view.Draft.Tagline);
        Assert.Equal("#0B5FA5", view.Draft.Color);
        Assert.Null(view.Published);
        Assert.Equal(new[] { "color", "tagline" }, view.Changes);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, view.SavedAt);

        await using var db = CreateDb();
        var row = await db.SupplierShowcaseContents.SingleAsync();
        Assert.Equal(OrgA, row.OrgId);
        Assert.Null(row.PublishedJson);
        Assert.Null(row.PublishedAt);
    }

    [Fact]
    public async Task ASupplier_NeverReadsOrChangesTheContentOfAnother()
    {
        await Service().SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Della A"}"""));

        // B sees an empty draft, saves its own, discards it: A keeps what it wrote.
        Assert.Null((await Service().GetAsync(OrgB)).Draft.Tagline);
        await Service().SaveDraftAsync(OrgB, 0, Patch("""{"tagline":"Della B"}"""));
        var discarded = await Service().DiscardAsync(OrgB, null);

        Assert.Null(discarded.Draft.Tagline);
        Assert.Equal("Della A", (await Service().GetAsync(OrgA)).Draft.Tagline);
    }

    [Fact]
    public async Task Save_AStaleVersion_IsA409_AndTheStoredDraftIsNotTouched()
    {
        await Service().SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Prima"}"""));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Service().SaveDraftAsync(OrgA, 7, Patch("""{"tagline":"Dopo"}""")));

        Assert.Equal(ShowcaseContentErrors.VersionConflict, conflict.Code);
        Assert.Equal("Prima", (await Service().GetAsync(OrgA)).Draft.Tagline);
    }

    [Fact]
    public async Task Save_AnInvalidValue_IsA422_AndNothingIsSaved()
    {
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Ciao","color":"#FFFF00"}""")));

        Assert.Equal(ShowcaseContentErrors.Invalid, refused.Code);
        await using var db = CreateDb();
        Assert.Empty(await db.SupplierShowcaseContents.ToListAsync());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static JsonObject Patch(string json) => (JsonObject)JsonNode.Parse(json)!;

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>A service on its own context, as every request gets its own; no service of the catalog is active.</summary>
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
