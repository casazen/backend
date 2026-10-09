using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="SupplierServiceCatalogService"/> (SP-02, <c>api/supplier/services</c>) on an in-memory database and the
/// filesystem storage: create, read, replace, delete, publish, pause, duplicate and photos; validation, publication
/// requirements, slugs unique per supplier, soft delete, the limit per supplier, the photo rules, and that a supplier never
/// reaches the services of another. What needs PostgreSQL (the unique index, the lock, <c>xmin</c>, parallel requests) is
/// in <c>SupplierServiceCatalogPostgresTests</c>.
/// </summary>
public class SupplierServiceCatalogServiceTests : IDisposable
{
    private const string PublicBaseUrl = "https://storage.test/public";

    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly byte[] WebpBytes =
        [0x52, 0x49, 0x46, 0x46, 0x1A, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x4C, 0x0D, 0x00, 0x00, 0x00];

    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "casazen-test-service-photos", Guid.NewGuid().ToString("N"));
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
    private readonly FileSystemFileStorage _storage;
    private readonly HookedStorage _hookedStorage;
    private readonly ImageStorageService _images;

    public SupplierServiceCatalogServiceTests()
    {
        var options = Options.Create(new StorageOptions
        {
            Provider = StorageOptions.FileSystemProvider,
            PublicBaseUrl = PublicBaseUrl,
            FileSystem = new FileSystemStorageOptions { RootPath = _root },
        });
        _storage = new FileSystemFileStorage(options, NullLogger<FileSystemFileStorage>.Instance);
        _hookedStorage = new HookedStorage(_storage);
        _images = new ImageStorageService(_hookedStorage, options, NullLogger<ImageStorageService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ─── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidInput_CreatesADraftWithSlugPositionAndTimestamps()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia cambio ospiti", priceFromCents: 4500, durationMinutes: 120));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(OrgA, created.OrgId);
        Assert.Equal("pulizia-cambio-ospiti", created.Slug);
        Assert.Equal(SupplierServiceListingStatus.Draft, created.Status);
        Assert.Equal(0, created.SortOrder);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, created.CreatedAt);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.Null(created.DeletedAt);
        Assert.Equal("[]", created.PhotoUrlsJson);

        var stored = await ReloadAsync(created.Id);
        Assert.Equal("Pulizia cambio ospiti", stored.Name);
        Assert.Equal(4500, stored.PriceFromCents);
        Assert.Equal(120, stored.DurationMinutes);
    }

    [Fact]
    public async Task CreateAsync_OnlyNameAndCategory_IsAnIncompleteDraftThatCannotBePublishedYet()
    {
        var created = await Service().CreateAsync(OrgA, Input("Ripasso pre-arrivo"));

        Assert.Equal(SupplierServiceListingStatus.Draft, created.Status);
        Assert.Equal(new[] { "durationMinutes", "priceFromCents" }, SupplierServiceListingRules.MissingForPublication(created));
    }

    [Fact]
    public async Task CreateAsync_TheSameNameTwiceInOneCatalog_GivesDistinctSuffixedSlugs()
    {
        var first = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var second = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var third = await Service().CreateAsync(OrgA, Input("  PULIZIA  "));

        Assert.Equal(new[] { "pulizia", "pulizia-2", "pulizia-3" }, new[] { first.Slug, second.Slug, third.Slug });
    }

    [Fact]
    public async Task CreateAsync_TheSameNameForAnotherSupplier_KeepsTheSameSlug()
    {
        var a = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var b = await Service().CreateAsync(OrgB, Input("Pulizia"));

        Assert.Equal("pulizia", a.Slug);
        Assert.Equal("pulizia", b.Slug);
        Assert.Equal(OrgB, b.OrgId);
    }

    [Fact]
    public async Task CreateAsync_NameWithoutUsableCharacters_GetsTheServiceFallbackSlug()
    {
        var created = await Service().CreateAsync(OrgA, Input("€€€"));

        Assert.Equal("servizio", created.Slug);
    }

    [Fact]
    public async Task CreateAsync_NewServicesGoLastUnlessThePositionIsGiven()
    {
        var first = await Service().CreateAsync(OrgA, Input("Uno"));
        var second = await Service().CreateAsync(OrgA, Input("Due"));
        var pinned = await Service().CreateAsync(OrgA, Input("Tre", sortOrder: 40));
        var last = await Service().CreateAsync(OrgA, Input("Quattro"));

        Assert.Equal(new[] { 0, 1, 40, 41 }, new[] { first.SortOrder, second.SortOrder, pinned.SortOrder, last.SortOrder });
    }

    [Fact]
    public async Task CreateAsync_InvalidInput_ThrowsNamingTheFieldsAndSavesNothing()
    {
        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(
            () => Service().CreateAsync(OrgA, Input("", priceFromCents: -1)));

        Assert.Equal(SupplierServiceCatalogErrors.Invalid, ex.Code);
        Assert.Equal(new[] { "name", "priceFromCents" }, ex.Fields);
        Assert.Empty(await Service().ListAsync(OrgA));
    }

    [Fact]
    public async Task CreateAsync_UnknownCategory_ThrowsInvalidServiceCategory()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().CreateAsync(OrgA, Input("Pulizia", category: "Pulizie")));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
        Assert.Empty(await Service().ListAsync(OrgA));
    }

    [Fact]
    public async Task CreateAsync_WithPhotoUrls_IsInvalidBecauseAServiceStartsWithoutPhotos()
    {
        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(
            () => Service().CreateAsync(OrgA, Input("Pulizia") with { PhotoUrls = ["https://storage.test/public/x.jpg"] }));

        Assert.Equal(new[] { "photoUrls" }, ex.Fields);
    }

    [Fact]
    public async Task CreateAsync_AtTheLimit_ThrowsLimitReachedUntilAServiceIsDeleted()
    {
        for (var i = 0; i < SupplierServiceCatalogLimits.MaxServicesPerSupplier; i++)
            await Service().CreateAsync(OrgA, Input($"Servizio {i}"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service().CreateAsync(OrgA, Input("Uno di troppo")));
        Assert.Equal(SupplierServiceCatalogErrors.LimitReached, ex.Code);
        Assert.Equal(SupplierServiceCatalogLimits.MaxServicesPerSupplier, Assert.Single(ex.MessageArgs));

        // Another supplier has its own count.
        await Service().CreateAsync(OrgB, Input("Il mio primo"));

        // A deleted service does not count.
        var list = await Service().ListAsync(OrgA);
        await Service().DeleteAsync(OrgA, list[0].Id);
        var created = await Service().CreateAsync(OrgA, Input("Ora c'è posto"));
        Assert.Equal("ora-c-e-posto", created.Slug);
    }

    // ─── Read ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheSuppliersNotDeletedServicesByPositionThenCreation()
    {
        var late = await Service().CreateAsync(OrgA, Input("Ultimo", sortOrder: 9));
        var early = await Service().CreateAsync(OrgA, Input("Primo", sortOrder: 1));
        var deleted = await Service().CreateAsync(OrgA, Input("Eliminato", sortOrder: 0));
        await Service().DeleteAsync(OrgA, deleted.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var sameAsEarly = await Service().CreateAsync(OrgA, Input("Pari merito", sortOrder: 1));
        await Service().CreateAsync(OrgB, Input("Di un altro fornitore", sortOrder: 0));

        var list = await Service().ListAsync(OrgA);

        Assert.Equal(new[] { early.Id, sameAsEarly.Id, late.Id }, list.Select(l => l.Id));
    }

    [Fact]
    public async Task GetAsync_AServiceOfAnotherSupplierOrADeletedOne_IsNotFound()
    {
        var mine = await Service().CreateAsync(OrgA, Input("Mio"));
        var gone = await Service().CreateAsync(OrgA, Input("Eliminato"));
        await Service().DeleteAsync(OrgA, gone.Id);

        Assert.Equal(mine.Id, (await Service().GetAsync(OrgA, mine.Id)).Id);
        await AssertNotFoundAsync(Service().GetAsync(OrgB, mine.Id));
        await AssertNotFoundAsync(Service().GetAsync(OrgA, gone.Id));
        await AssertNotFoundAsync(Service().GetAsync(OrgA, Guid.NewGuid()));
    }

    // ─── Update ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ReplacesTheContentAndKeepsStatusPhotosPositionAndCreationDate()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", sortOrder: 5));
        var withPhoto = await Service().AddPhotosAsync(OrgA, created.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        _clock.Advance(TimeSpan.FromHours(1));

        var updated = await Service().UpdateAsync(
            OrgA,
            created.Id,
            withPhoto.Version,
            Input(
                "Pulizia profonda",
                priceFromCents: 12000,
                durationMinutes: 240,
                summary: "Vetri, forno e frigo.",
                includeVat: true,
                weekdays: [DayOfWeek.Tuesday, DayOfWeek.Thursday]));

        Assert.Equal("Pulizia profonda", updated.Name);
        Assert.Equal(12000, updated.PriceFromCents);
        Assert.Equal(240, updated.DurationMinutes);
        Assert.Equal("Vetri, forno e frigo.", updated.Summary);
        Assert.True(updated.PricesIncludeVat);
        Assert.Equal(0b0001010, updated.WeekdaysMask);
        Assert.Equal(5, updated.SortOrder);
        Assert.Equal(SupplierServiceListingStatus.Draft, updated.Status);
        Assert.Equal(withPhoto.PhotoUrlsJson, updated.PhotoUrlsJson);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, updated.UpdatedAt);
        Assert.Equal(updated.PhotoUrlsJson, (await ReloadAsync(created.Id)).PhotoUrlsJson);
    }

    [Fact]
    public async Task UpdateAsync_AFieldLeftOut_TakesItsDefault()
    {
        var created = await Service().CreateAsync(
            OrgA,
            Input("Pulizia", priceFromCents: 4500, durationMinutes: 90, summary: "Breve", weekdays: [DayOfWeek.Monday]));

        var updated = await Service().UpdateAsync(OrgA, created.Id, created.Version, Input("Pulizia"));

        Assert.Null(updated.PriceFromCents);
        Assert.Null(updated.DurationMinutes);
        Assert.Null(updated.Summary);
        Assert.Equal(SupplierServiceWeekdays.AllMask, updated.WeekdaysMask);
    }

    [Fact]
    public async Task UpdateAsync_StaleVersion_ThrowsChangedAndSavesNothing()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => Service().UpdateAsync(OrgA, created.Id, created.Version + 99, Input("Nuovo nome")));

        Assert.Equal(SupplierServiceCatalogErrors.Changed, ex.Code);
        Assert.Equal("Pulizia", (await ReloadAsync(created.Id)).Name);
    }

    [Fact]
    public async Task UpdateAsync_InvalidInput_ThrowsAndLeavesTheServiceAsItWas()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", priceFromCents: 3000, durationMinutes: 60));

        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(
            () => Service().UpdateAsync(OrgA, created.Id, created.Version, Input("Nuovo", durationMinutes: 1)));

        Assert.Equal(new[] { "durationMinutes" }, ex.Fields);
        var stored = await ReloadAsync(created.Id);
        Assert.Equal("Pulizia", stored.Name);
        Assert.Equal(60, stored.DurationMinutes);
    }

    [Fact]
    public async Task UpdateAsync_ADraftFollowsItsNameInTheSlug_APublishedOneNever()
    {
        var draft = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var renamed = await Service().UpdateAsync(OrgA, draft.Id, draft.Version, Input("Pulizia profonda"));
        Assert.Equal("pulizia-profonda", renamed.Slug);

        // The same name again: the slug does not move.
        var again = await Service().UpdateAsync(OrgA, draft.Id, renamed.Version, Input("Pulizia profonda", summary: "Altro"));
        Assert.Equal("pulizia-profonda", again.Slug);

        var published = await Service().CreateAsync(OrgA, Input("Sanificazione", priceFromCents: 8000, durationMinutes: 120));
        await Service().PublishAsync(OrgA, published.Id);
        var current = await Service().GetAsync(OrgA, published.Id);
        var renamedPublished = await Service().UpdateAsync(
            OrgA, published.Id, current.Version, Input("Sanificazione totale", priceFromCents: 8000, durationMinutes: 120));
        Assert.Equal("sanificazione", renamedPublished.Slug);
    }

    [Fact]
    public async Task UpdateAsync_ADraftRenamedToANameAnotherServiceUses_GetsTheNextFreeSlug()
    {
        await Service().CreateAsync(OrgA, Input("Pulizia"));
        var other = await Service().CreateAsync(OrgA, Input("Altro"));

        var renamed = await Service().UpdateAsync(OrgA, other.Id, other.Version, Input("Pulizia"));

        Assert.Equal("pulizia-2", renamed.Slug);
    }

    [Fact]
    public async Task UpdateAsync_APublishedServiceMadeIncomplete_ThrowsNotPublishableAndKeepsItComplete()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", priceFromCents: 3000, durationMinutes: 60));
        var published = await Service().PublishAsync(OrgA, created.Id);

        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(
            () => Service().UpdateAsync(OrgA, created.Id, published.Version, Input("Pulizia", priceFromCents: 3000)));

        Assert.Equal(SupplierServiceCatalogErrors.NotPublishable, ex.Code);
        Assert.Equal(new[] { "durationMinutes" }, ex.Fields);
        Assert.Equal(60, (await ReloadAsync(created.Id)).DurationMinutes);
    }

    [Fact]
    public async Task UpdateAsync_ADraftOrAPausedServiceMayStayIncomplete()
    {
        var draft = await Service().CreateAsync(OrgA, Input("Bozza", priceFromCents: 3000, durationMinutes: 60));
        var emptied = await Service().UpdateAsync(OrgA, draft.Id, draft.Version, Input("Bozza"));
        Assert.Null(emptied.DurationMinutes);

        var created = await Service().CreateAsync(OrgA, Input("Pausa", priceFromCents: 3000, durationMinutes: 60));
        await Service().PublishAsync(OrgA, created.Id);
        var paused = await Service().PauseAsync(OrgA, created.Id);
        var pausedEmptied = await Service().UpdateAsync(OrgA, created.Id, paused.Version, Input("Pausa"));
        Assert.Equal(SupplierServiceListingStatus.Paused, pausedEmptied.Status);
        Assert.Null(pausedEmptied.PriceFromCents);

        // ...but cannot be published again like that.
        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(() => Service().PublishAsync(OrgA, created.Id));
        Assert.Equal(new[] { "durationMinutes", "priceFromCents" }, ex.Fields);
    }

    [Fact]
    public async Task UpdateAsync_AServiceOfAnotherSupplier_IsNotFoundAndChangesNothing()
    {
        var mine = await Service().CreateAsync(OrgA, Input("Mio"));

        await AssertNotFoundAsync(Service().UpdateAsync(OrgB, mine.Id, mine.Version, Input("Rubato")));

        Assert.Equal("Mio", (await ReloadAsync(mine.Id)).Name);
    }

    [Fact]
    public async Task UpdateAsync_SortOrder_IsAppliedWhenGivenAndKeptWhenNot()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", sortOrder: 5));

        var kept = await Service().UpdateAsync(OrgA, created.Id, created.Version, Input("Pulizia"));
        Assert.Equal(5, kept.SortOrder);

        var moved = await Service().UpdateAsync(OrgA, created.Id, kept.Version, Input("Pulizia", sortOrder: 2));
        Assert.Equal(2, moved.SortOrder);
    }

    // ─── Update: the photo list ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_PhotoUrls_AreTheOnesToKeepInTheNewOrder_AndTheOthersAreRemovedFromTheStorage()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var withPhotos = await Service().AddPhotosAsync(
            OrgA,
            created.Id,
            [Image("a.jpg", "image/jpeg", JpegBytes), Image("b.png", "image/png", PngBytes), Image("c.webp", "image/webp", WebpBytes)]);
        var urls = SupplierServiceListingJson.ReadStrings(withPhotos.PhotoUrlsJson);
        Assert.Equal(3, urls.Count);

        var updated = await Service().UpdateAsync(
            OrgA, created.Id, withPhotos.Version, Input("Pulizia") with { PhotoUrls = [urls[2], urls[0]] });

        Assert.Equal(new[] { urls[2], urls[0] }, SupplierServiceListingJson.ReadStrings(updated.PhotoUrlsJson));
        Assert.True(await ObjectExistsAsync(urls[0]));
        Assert.True(await ObjectExistsAsync(urls[2]));
        Assert.False(await ObjectExistsAsync(urls[1]));
    }

    [Fact]
    public async Task UpdateAsync_PhotoUrlsNull_KeepsThePhotos_AndAnEmptyListRemovesThemAll()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var withPhoto = await Service().AddPhotosAsync(OrgA, created.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        var url = Assert.Single(SupplierServiceListingJson.ReadStrings(withPhoto.PhotoUrlsJson));

        var kept = await Service().UpdateAsync(OrgA, created.Id, withPhoto.Version, Input("Pulizia") with { PhotoUrls = null });
        Assert.Equal(withPhoto.PhotoUrlsJson, kept.PhotoUrlsJson);
        Assert.True(await ObjectExistsAsync(url));

        var emptied = await Service().UpdateAsync(OrgA, created.Id, kept.Version, Input("Pulizia") with { PhotoUrls = [] });
        Assert.Equal("[]", emptied.PhotoUrlsJson);
        Assert.False(await ObjectExistsAsync(url));
    }

    [Fact]
    public async Task UpdateAsync_APhotoThatIsNotTheServicesOwn_IsInvalidAndNothingIsSaved()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var withPhoto = await Service().AddPhotosAsync(OrgA, created.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        var url = Assert.Single(SupplierServiceListingJson.ReadStrings(withPhoto.PhotoUrlsJson));

        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(
            () => Service().UpdateAsync(
                OrgA,
                created.Id,
                withPhoto.Version,
                Input("Cambiato") with { PhotoUrls = [url, "https://tracker.example/pixel.gif"] }));

        Assert.Equal(new[] { "photoUrls" }, ex.Fields);
        var stored = await ReloadAsync(created.Id);
        Assert.Equal("Pulizia", stored.Name);
        Assert.Equal(withPhoto.PhotoUrlsJson, stored.PhotoUrlsJson);
        Assert.True(await ObjectExistsAsync(url));
    }

    [Fact]
    public async Task UpdateAsync_APhotoOfAnotherSuppliersService_CannotBeAdded()
    {
        var mine = await Service().CreateAsync(OrgA, Input("Mio"));
        var theirs = await Service().CreateAsync(OrgB, Input("Loro"));
        var theirPhoto = await Service().AddPhotosAsync(OrgB, theirs.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        var url = Assert.Single(SupplierServiceListingJson.ReadStrings(theirPhoto.PhotoUrlsJson));

        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(
            () => Service().UpdateAsync(OrgA, mine.Id, mine.Version, Input("Mio") with { PhotoUrls = [url] }));

        Assert.Equal(new[] { "photoUrls" }, ex.Fields);
        Assert.Equal("[]", (await ReloadAsync(mine.Id)).PhotoUrlsJson);
    }

    // ─── Delete ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_IsSoft_HidesTheServiceAndFreesItsSlug()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        _clock.Advance(TimeSpan.FromMinutes(5));

        await Service().DeleteAsync(OrgA, created.Id);

        var row = await ReloadAsync(created.Id);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, row.DeletedAt);
        Assert.Equal("pulizia", row.Slug);
        Assert.Empty(await Service().ListAsync(OrgA));
        await AssertNotFoundAsync(Service().GetAsync(OrgA, created.Id));

        var again = await Service().CreateAsync(OrgA, Input("Pulizia"));
        Assert.Equal("pulizia", again.Slug);
    }

    [Fact]
    public async Task DeleteAsync_ADeletedOrForeignService_IsNotFound()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        await AssertNotFoundAsync(Service().DeleteAsync(OrgB, created.Id));
        Assert.NotNull(await Service().GetAsync(OrgA, created.Id));

        await Service().DeleteAsync(OrgA, created.Id);
        await AssertNotFoundAsync(Service().DeleteAsync(OrgA, created.Id));
    }

    [Fact]
    public async Task DeleteAsync_KeepsThePhotoObjects_TheRowIsStillOnFile()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var withPhoto = await Service().AddPhotosAsync(OrgA, created.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        var url = Assert.Single(SupplierServiceListingJson.ReadStrings(withPhoto.PhotoUrlsJson));

        await Service().DeleteAsync(OrgA, created.Id);

        Assert.True(await ObjectExistsAsync(url));
        Assert.Equal(withPhoto.PhotoUrlsJson, (await ReloadAsync(created.Id)).PhotoUrlsJson);
    }

    // ─── Publish and pause ───────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_ACompleteDraft_BecomesActive()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", priceFromCents: 4500, durationMinutes: 120));
        _clock.Advance(TimeSpan.FromMinutes(1));

        var published = await Service().PublishAsync(OrgA, created.Id);

        Assert.Equal(SupplierServiceListingStatus.Active, published.Status);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, published.UpdatedAt);
        Assert.Equal(SupplierServiceListingStatus.Active, (await ReloadAsync(created.Id)).Status);
    }

    [Fact]
    public async Task PublishAsync_AQuoteInPlaceOfAPrice_Publishes()
    {
        var created = await Service().CreateAsync(OrgA, Input("Impianto elettrico", category: "electrical", durationMinutes: 180, requiresQuote: true));

        var published = await Service().PublishAsync(OrgA, created.Id);

        Assert.Equal(SupplierServiceListingStatus.Active, published.Status);
        Assert.Null(published.PriceFromCents);
        Assert.Equal(ServiceCategories.Electrical, published.Category);
    }

    [Fact]
    public async Task PublishAsync_IncompleteDraft_ThrowsNotPublishableNamingWhatIsMissingAndStaysADraft()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        var ex = await Assert.ThrowsAsync<SupplierServiceRuleException>(() => Service().PublishAsync(OrgA, created.Id));

        Assert.Equal(SupplierServiceCatalogErrors.NotPublishable, ex.Code);
        Assert.Equal(new[] { "durationMinutes", "priceFromCents" }, ex.Fields);
        Assert.Equal(SupplierServiceListingStatus.Draft, (await ReloadAsync(created.Id)).Status);
    }

    [Fact]
    public async Task PublishAsync_AnActiveService_IsReturnedAsItIsWithoutTouchingIt()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", priceFromCents: 4500, durationMinutes: 120));
        var published = await Service().PublishAsync(OrgA, created.Id);
        _clock.Advance(TimeSpan.FromHours(3));

        var again = await Service().PublishAsync(OrgA, created.Id);

        Assert.Equal(SupplierServiceListingStatus.Active, again.Status);
        Assert.Equal(published.UpdatedAt, again.UpdatedAt);
    }

    [Fact]
    public async Task PauseAsync_ThenPublishAgain_TakesAServiceOffAndBack()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", priceFromCents: 4500, durationMinutes: 120));
        await Service().PublishAsync(OrgA, created.Id);

        var paused = await Service().PauseAsync(OrgA, created.Id);
        Assert.Equal(SupplierServiceListingStatus.Paused, paused.Status);

        var again = await Service().PublishAsync(OrgA, created.Id);
        Assert.Equal(SupplierServiceListingStatus.Active, again.Status);
    }

    [Fact]
    public async Task PauseAsync_ADraft_ThrowsCannotPause_AndAPausedServiceIsReturnedAsItIs()
    {
        var draft = await Service().CreateAsync(OrgA, Input("Bozza", priceFromCents: 4500, durationMinutes: 120));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service().PauseAsync(OrgA, draft.Id));
        Assert.Equal(SupplierServiceCatalogErrors.CannotPause, ex.Code);

        await Service().PublishAsync(OrgA, draft.Id);
        var paused = await Service().PauseAsync(OrgA, draft.Id);
        _clock.Advance(TimeSpan.FromHours(1));
        var again = await Service().PauseAsync(OrgA, draft.Id);
        Assert.Equal(paused.UpdatedAt, again.UpdatedAt);
        Assert.Equal(SupplierServiceListingStatus.Paused, again.Status);
    }

    [Fact]
    public async Task PublishAndPause_AServiceOfAnotherSupplier_AreNotFound()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia", priceFromCents: 4500, durationMinutes: 120));
        await Service().PublishAsync(OrgA, created.Id);

        await AssertNotFoundAsync(Service().PublishAsync(OrgB, created.Id));
        await AssertNotFoundAsync(Service().PauseAsync(OrgB, created.Id));
        Assert.Equal(SupplierServiceListingStatus.Active, (await ReloadAsync(created.Id)).Status);
    }

    // ─── Duplicate ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DuplicateAsync_CopiesTheContentAsADraftWithANewSlugTheCopySuffixAndTheLastPosition()
    {
        var source = await Service().CreateAsync(
            OrgA,
            Input(
                "Pulizia cambio ospiti",
                priceFromCents: 4500,
                durationMinutes: 120,
                summary: "Breve",
                includeVat: true,
                weekdays: [DayOfWeek.Monday, DayOfWeek.Friday],
                supplements: [new SupplierServiceSupplement("bagno", "Bagno in più", 1000, "bathroom", 3)],
                included: ["Biancheria"],
                excluded: ["Tende"]));
        await Service().AddPhotosAsync(OrgA, source.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        var active = await Service().PublishAsync(OrgA, source.Id);

        var copy = await Service().DuplicateAsync(OrgA, source.Id, " (copia)");

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(OrgA, copy.OrgId);
        Assert.Equal("Pulizia cambio ospiti (copia)", copy.Name);
        Assert.Equal("pulizia-cambio-ospiti-copia", copy.Slug);
        Assert.Equal(SupplierServiceListingStatus.Draft, copy.Status);
        Assert.Equal(SupplierServiceListingStatus.Active, (await ReloadAsync(source.Id)).Status);
        Assert.Equal(1, copy.SortOrder);
        Assert.Equal(active.Category, copy.Category);
        Assert.Equal(4500, copy.PriceFromCents);
        Assert.Equal(120, copy.DurationMinutes);
        Assert.Equal("Breve", copy.Summary);
        Assert.True(copy.PricesIncludeVat);
        Assert.Equal(active.WeekdaysMask, copy.WeekdaysMask);
        Assert.Equal(active.SupplementsJson, copy.SupplementsJson);
        Assert.Equal(active.IncludedJson, copy.IncludedJson);
        Assert.Equal(active.ExcludedJson, copy.ExcludedJson);
        Assert.Equal(active.PhotoUrlsJson, copy.PhotoUrlsJson);
        Assert.Null(copy.DeletedAt);
        Assert.Equal(2, (await Service().ListAsync(OrgA)).Count);
    }

    [Fact]
    public async Task DuplicateAsync_Twice_GivesDistinctNamesAndSlugsAreNeverRepeated()
    {
        var source = await Service().CreateAsync(OrgA, Input("Pulizia"));

        var first = await Service().DuplicateAsync(OrgA, source.Id, " (copy)");
        var second = await Service().DuplicateAsync(OrgA, source.Id, " (copy)");

        Assert.Equal(new[] { "pulizia", "pulizia-copy", "pulizia-copy-2" }, new[] { source.Slug, first.Slug, second.Slug });
        Assert.Equal("Pulizia (copy)", first.Name);
        Assert.Equal("Pulizia (copy)", second.Name);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { source.SortOrder, first.SortOrder, second.SortOrder });
    }

    [Fact]
    public async Task DuplicateAsync_ALongName_IsShortenedSoTheCopyFitsTheLimit()
    {
        var source = await Service().CreateAsync(OrgA, Input(new string('a', SupplierServiceCatalogLimits.NameMaxLength)));

        var copy = await Service().DuplicateAsync(OrgA, source.Id, " (copia)");

        Assert.Equal(SupplierServiceCatalogLimits.NameMaxLength, copy.Name.Length);
        Assert.EndsWith(" (copia)", copy.Name);
    }

    [Fact]
    public async Task DuplicateAsync_AtTheLimit_ThrowsLimitReached()
    {
        var first = await Service().CreateAsync(OrgA, Input("Uno"));
        for (var i = 1; i < SupplierServiceCatalogLimits.MaxServicesPerSupplier; i++)
            await Service().CreateAsync(OrgA, Input($"Servizio {i}"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service().DuplicateAsync(OrgA, first.Id, " (copia)"));

        Assert.Equal(SupplierServiceCatalogErrors.LimitReached, ex.Code);
    }

    [Fact]
    public async Task DuplicateAsync_AServiceOfAnotherSupplierOrADeletedOne_IsNotFound()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var gone = await Service().CreateAsync(OrgA, Input("Eliminato"));
        await Service().DeleteAsync(OrgA, gone.Id);

        await AssertNotFoundAsync(Service().DuplicateAsync(OrgB, created.Id, " (copia)"));
        await AssertNotFoundAsync(Service().DuplicateAsync(OrgA, gone.Id, " (copia)"));
        Assert.Single(await Service().ListAsync(OrgA));
        Assert.Empty(await Service().ListAsync(OrgB));
    }

    [Fact]
    public async Task DuplicateAsync_ThePhotosAreSharedSoAnObjectGoesOnlyWhenNoServiceUsesIt()
    {
        var source = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var withPhoto = await Service().AddPhotosAsync(OrgA, source.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        var url = Assert.Single(SupplierServiceListingJson.ReadStrings(withPhoto.PhotoUrlsJson));
        var copy = await Service().DuplicateAsync(OrgA, source.Id, " (copia)");
        Assert.Equal(withPhoto.PhotoUrlsJson, copy.PhotoUrlsJson);

        // The copy drops the photo: the original still lists it, so the object stays.
        await Service().UpdateAsync(OrgA, copy.Id, copy.Version, Input("Pulizia (copia)") with { PhotoUrls = [] });
        Assert.True(await ObjectExistsAsync(url));

        // The original drops it too: nobody lists it any more, the object goes.
        await Service().UpdateAsync(OrgA, source.Id, withPhoto.Version, Input("Pulizia") with { PhotoUrls = [] });
        Assert.False(await ObjectExistsAsync(url));
    }

    // ─── Photos ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddPhotosAsync_ValidImages_AreStoredInTheSuppliersFolderAndAppendedInOrder()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        var updated = await Service().AddPhotosAsync(
            OrgA,
            created.Id,
            [Image("facciata.JPG", "image/jpeg", JpegBytes), Image("salotto.png", "image/png", PngBytes), Image("bagno.webp", "image/webp", WebpBytes)]);

        var urls = SupplierServiceListingJson.ReadStrings(updated.PhotoUrlsJson);
        Assert.Equal(3, urls.Count);
        Assert.EndsWith(".jpg", urls[0]);
        Assert.EndsWith(".png", urls[1]);
        Assert.EndsWith(".webp", urls[2]);
        foreach (var url in urls)
        {
            Assert.StartsWith($"{PublicBaseUrl}/suppliers/{OrgA}/photos/", url);
            Assert.True(await ObjectExistsAsync(url));
        }

        Assert.Equal(updated.PhotoUrlsJson, (await ReloadAsync(created.Id)).PhotoUrlsJson);
    }

    [Fact]
    public async Task AddPhotosAsync_WithPhotosAlready_KeepsThemFirst()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var first = await Service().AddPhotosAsync(OrgA, created.Id, [Image("uno.jpg", "image/jpeg", JpegBytes)]);

        var second = await Service().AddPhotosAsync(OrgA, created.Id, [Image("due.png", "image/png", PngBytes)]);

        var urls = SupplierServiceListingJson.ReadStrings(second.PhotoUrlsJson);
        Assert.Equal(2, urls.Count);
        Assert.Equal(SupplierServiceListingJson.ReadStrings(first.PhotoUrlsJson)[0], urls[0]);
        Assert.EndsWith(".png", urls[1]);
    }

    [Fact]
    public async Task AddPhotosAsync_NoFile_ThrowsPhotoNone()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service().AddPhotosAsync(OrgA, created.Id, []));

        Assert.Equal(SupplierServiceCatalogErrors.PhotoNone, ex.Code);
    }

    [Fact]
    public async Task AddPhotosAsync_ATextRenamedToJpg_IsRefusedOnItsContent_AndNothingIsStored()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var disguised = "ciao, non sono una foto"u8.ToArray();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().AddPhotosAsync(OrgA, created.Id, [Image("ok.jpg", "image/jpeg", JpegBytes), Image("finta.jpg", "image/jpeg", disguised)]));

        Assert.Equal(SupplierServiceCatalogErrors.PhotoInvalidType, ex.Code);
        Assert.Equal("finta.jpg", ex.MessageArgs[0]);
        Assert.Empty(StoredObjects());
        Assert.Equal("[]", (await ReloadAsync(created.Id)).PhotoUrlsJson);
    }

    [Theory]
    [InlineData("foto.gif", "image/gif")]
    [InlineData("foto.pdf", "application/pdf")]
    [InlineData("foto.jpg", "image/png")] // the declared type and the extension must agree with the content
    [InlineData("foto.png", "image/jpeg")]
    public async Task AddPhotosAsync_UnsupportedOrInconsistentType_ThrowsInvalidType(string fileName, string contentType)
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().AddPhotosAsync(OrgA, created.Id, [Image(fileName, contentType, JpegBytes)]));

        Assert.Equal(SupplierServiceCatalogErrors.PhotoInvalidType, ex.Code);
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task AddPhotosAsync_EmptyOrOversizedFile_ThrowsInvalidSize()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().AddPhotosAsync(OrgA, created.Id, [Image("vuota.jpg", "image/jpeg", [])]));
        var huge = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().AddPhotosAsync(OrgA, created.Id, [FakeSized("grande.jpg", "image/jpeg", SupplierServiceCatalogLimits.MaxPhotoFileSizeBytes + 1)]));

        Assert.Equal(SupplierServiceCatalogErrors.PhotoInvalidSize, empty.Code);
        Assert.Equal(SupplierServiceCatalogErrors.PhotoInvalidSize, huge.Code);
        Assert.Equal("grande.jpg", huge.MessageArgs[0]);
        Assert.Equal(10L, huge.MessageArgs[1]);
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task AddPhotosAsync_OverTheLimitOfPhotos_ThrowsLimitReachedAndStoresNothing()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var files = Enumerable.Range(0, SupplierServiceCatalogLimits.MaxPhotos)
            .Select(i => Image($"foto-{i}.jpg", "image/jpeg", JpegBytes))
            .ToList();
        await Service().AddPhotosAsync(OrgA, created.Id, files);
        var before = StoredObjects().Length;

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => Service().AddPhotosAsync(OrgA, created.Id, [Image("una-di-troppo.jpg", "image/jpeg", JpegBytes)]));

        Assert.Equal(SupplierServiceCatalogErrors.PhotoLimitReached, ex.Code);
        Assert.Equal(SupplierServiceCatalogLimits.MaxPhotos, ex.MessageArgs[0]);
        Assert.Equal(SupplierServiceCatalogLimits.MaxPhotos, ex.MessageArgs[1]);
        Assert.Equal(before, StoredObjects().Length);
        Assert.Equal(SupplierServiceCatalogLimits.MaxPhotos, SupplierServiceListingJson.ReadStrings((await ReloadAsync(created.Id)).PhotoUrlsJson).Count);
    }

    [Fact]
    public async Task AddPhotosAsync_MoreFilesThanTheServiceCanHold_ThrowsBeforeStoringAnything()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var files = Enumerable.Range(0, SupplierServiceCatalogLimits.MaxPhotos + 1)
            .Select(i => Image($"foto-{i}.jpg", "image/jpeg", JpegBytes))
            .ToList();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service().AddPhotosAsync(OrgA, created.Id, files));

        Assert.Equal(SupplierServiceCatalogErrors.PhotoLimitReached, ex.Code);
        Assert.Empty(StoredObjects());
    }

    [Fact]
    public async Task AddPhotosAsync_TheStorageFailsMidway_RemovesTheObjectsAlreadyStored()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var puts = 0;
        _hookedStorage.BeforePut = () =>
        {
            if (++puts == 2)
                throw new IOException("storage unavailable");
        };

        await Assert.ThrowsAsync<IOException>(
            () => Service().AddPhotosAsync(
                OrgA,
                created.Id,
                [Image("a.jpg", "image/jpeg", JpegBytes), Image("b.png", "image/png", PngBytes), Image("c.webp", "image/webp", WebpBytes)]));

        Assert.Empty(StoredObjects());
        Assert.Equal("[]", (await ReloadAsync(created.Id)).PhotoUrlsJson);
    }

    [Fact]
    public async Task AddPhotosAsync_AServiceOfAnotherSupplier_IsNotFoundAndStoresNothing()
    {
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));

        await AssertNotFoundAsync(Service().AddPhotosAsync(OrgB, created.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]));

        Assert.Empty(StoredObjects());
        Assert.Equal("[]", (await ReloadAsync(created.Id)).PhotoUrlsJson);
    }

    [Fact]
    public async Task UpdateAsync_AnEntryThatIsNotAnObjectOfTheSuppliersOwnFolder_IsNeverDeletedFromTheStorage()
    {
        // The list can hold such an entry only through a row written by hand (a legacy path, an external address, an
        // object of another supplier): taking it off the list must not delete anything that is not the supplier's own.
        var created = await Service().CreateAsync(OrgA, Input("Pulizia"));
        var theirs = await Service().CreateAsync(OrgB, Input("Loro"));
        var theirPhoto = await Service().AddPhotosAsync(OrgB, theirs.Id, [Image("a.jpg", "image/jpeg", JpegBytes)]);
        var foreignUrl = Assert.Single(SupplierServiceListingJson.ReadStrings(theirPhoto.PhotoUrlsJson));
        const string external = "https://cdn.example/foto.jpg";
        await using (var db = CreateDb())
        {
            var row = await db.SupplierServiceListings.SingleAsync(l => l.Id == created.Id);
            row.PhotoUrlsJson = SupplierServiceListingJson.Serialize(new[] { external, foreignUrl });
            await db.SaveChangesAsync();
        }

        var updated = await Service().UpdateAsync(OrgA, created.Id, created.Version, Input("Pulizia") with { PhotoUrls = [] });

        Assert.Equal("[]", updated.PhotoUrlsJson);
        Assert.True(await ObjectExistsAsync(foreignUrl));
        Assert.Single(StoredObjects());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>A service on its own context, as every request gets its own.</summary>
    private SupplierServiceCatalogService Service() =>
        new(CreateDb(), _hookedStorage, _images, NullLogger<SupplierServiceCatalogService>.Instance, _clock);

    private async Task<SupplierServiceListing> ReloadAsync(Guid id)
    {
        await using var db = CreateDb();
        return await db.SupplierServiceListings.AsNoTracking().SingleAsync(l => l.Id == id);
    }

    private static SupplierServiceListingInput Input(
        string name,
        string category = ServiceCategories.Cleaning,
        int? priceFromCents = null,
        int? durationMinutes = null,
        string? summary = null,
        bool includeVat = false,
        bool requiresQuote = false,
        IReadOnlyCollection<DayOfWeek>? weekdays = null,
        IReadOnlyList<SupplierServiceSupplement?>? supplements = null,
        IReadOnlyList<string?>? included = null,
        IReadOnlyList<string?>? excluded = null,
        int? sortOrder = null) =>
        new(
            name,
            category,
            summary,
            null,
            priceFromCents,
            SupplierServicePriceUnit.PerJob,
            includeVat,
            requiresQuote,
            durationMinutes,
            null,
            weekdays,
            supplements,
            included,
            excluded,
            null,
            sortOrder);

    private static async Task AssertNotFoundAsync(Task task)
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => task);
        Assert.Equal(SupplierServiceCatalogErrors.NotFound, ex.Code);
        Assert.Equal("SupplierServiceNotFound", ex.MessageKey);
    }

    private static async Task AssertNotFoundAsync<T>(Task<T> task) => await AssertNotFoundAsync((Task)task);

    private async Task<bool> ObjectExistsAsync(string url)
    {
        var key = _storage.TryGetPublicKey(url);
        return key is not null && await _storage.ExistsAsync(StorageBucket.Public, key);
    }

    private string[] StoredObjects() =>
        Directory.Exists(_storage.BucketRoot(StorageBucket.Public))
            ? Directory.GetFiles(_storage.BucketRoot(StorageBucket.Public), "*", SearchOption.AllDirectories)
            : [];

    private static IFormFile Image(string fileName, string contentType, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "photos", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    /// <summary>A file that claims to be <paramref name="length"/> bytes long without allocating them.</summary>
    private static IFormFile FakeSized(string fileName, string contentType, long length) =>
        new FormFile(new MemoryStream(JpegBytes), 0, length, "photos", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    /// <summary>The filesystem storage with a hook that runs before each upload (and can make it fail).</summary>
    private sealed class HookedStorage(IFileStorage inner) : IFileStorage
    {
        public Action? BeforePut { get; set; }

        public Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            BeforePut?.Invoke();
            return inner.PutAsync(bucket, key, content, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(bucket, key, cancellationToken);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(bucket, key, cancellationToken);

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(bucket, key, cancellationToken);

        public string GetPublicUrl(string key) => inner.GetPublicUrl(key);

        public string? TryGetPublicKey(string url) => inner.TryGetPublicKey(url);

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            inner.GetSignedReadUrlAsync(key, lifetime, downloadFileName, cancellationToken);
    }
}
