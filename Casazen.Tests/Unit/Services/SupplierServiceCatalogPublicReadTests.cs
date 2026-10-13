using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
/// SP-09: what the catalog gives to an anonymous read (<see cref="ISupplierServiceCatalogService.ListPublicAsync"/> and
/// <see cref="ISupplierServiceCatalogService.FindPublicAsync"/>) on an in-memory database: only the published services, not
/// deleted, of an active supplier and of the org asked for, in the supplier's order, in a type that has no field the console
/// keeps to itself.
/// </summary>
public class SupplierServiceCatalogPublicReadTests
{
    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly DateTime Created = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databaseName = Guid.NewGuid().ToString();

    [Fact]
    public async Task ListPublicAsync_MapsEveryPublicFieldOfAPublishedService()
    {
        await SeedSuppliersAsync();
        await SeedAsync(new SupplierServiceListing
        {
            OrgId = OrgA,
            Slug = "pulizia-profonda",
            Name = "Pulizia profonda",
            Category = ServiceCategories.Cleaning,
            Summary = "Stanza per stanza.",
            Description = "Descrizione lunga.",
            PriceFromCents = 4500,
            PriceUnit = SupplierServicePriceUnit.PerHour,
            PricesIncludeVat = true,
            RequiresQuote = false,
            DurationMinutes = 180,
            MinNoticeHours = 12,
            WeekdaysMask = SupplierServiceWeekdays.ToMask([DayOfWeek.Monday, DayOfWeek.Friday]),
            SupplementsJson = """[{"code":"bagno","label":"Bagno in più","amountCents":1000,"per":"bathroom","max":3},{"code":"ferro","label":"Ferro","amountCents":300,"per":"flat"}]""",
            IncludedJson = """["Bagni","Cucina"]""",
            ExcludedJson = """["Vetri"]""",
            PhotoUrlsJson = """["https://storage.test/a.jpg","https://storage.test/b.jpg"]""",
            Status = SupplierServiceListingStatus.Active,
        });

        var service = Assert.Single(await Catalog().ListPublicAsync(OrgA));

        Assert.Equal("pulizia-profonda", service.Slug);
        Assert.Equal("Pulizia profonda", service.Name);
        Assert.Equal(ServiceCategories.Cleaning, service.Category);
        Assert.Equal("Stanza per stanza.", service.Summary);
        Assert.Equal("Descrizione lunga.", service.Description);
        Assert.Equal(4500, service.PriceFromCents);
        Assert.Equal(SupplierServicePriceUnit.PerHour, service.PriceUnit);
        Assert.True(service.PricesIncludeVat);
        Assert.False(service.RequiresQuote);
        Assert.Equal(180, service.DurationMinutes);
        Assert.Equal(
            new[]
            {
                new SupplierServiceSupplement("bagno", "Bagno in più", 1000, "bathroom", 3),
                new SupplierServiceSupplement("ferro", "Ferro", 300, "flat", null),
            },
            service.Supplements);
        Assert.Equal(new[] { "Bagni", "Cucina" }, service.Included);
        Assert.Equal(new[] { "Vetri" }, service.Excluded);
        Assert.Equal(new[] { "https://storage.test/a.jpg", "https://storage.test/b.jpg" }, service.PhotoUrls);
        // The terms the slot planner reads travel with it, as the planning terms of the service.
        Assert.Equal(12, service.MinNoticeHours);
        Assert.Equal(SupplierServiceWeekdays.ToMask([DayOfWeek.Monday, DayOfWeek.Friday]), service.WeekdaysMask);
        Assert.Equal(new SupplierSlotQuery(180, 12, service.WeekdaysMask), service.ToSlotQuery());
    }

    [Fact]
    public async Task ListPublicAsync_OnlyPublishedNotDeletedServicesOfTheOrgAsked_InTheSuppliersOrder()
    {
        await SeedSuppliersAsync();
        await SeedAsync(Row(OrgA, "terza", sortOrder: 5));
        await SeedAsync(Row(OrgA, "prima", sortOrder: 1));
        await SeedAsync(Row(OrgA, "seconda-a", sortOrder: 2, created: Created));
        await SeedAsync(Row(OrgA, "seconda-b", sortOrder: 2, created: Created.AddMinutes(1)));
        await SeedAsync(Row(OrgA, "bozza", status: SupplierServiceListingStatus.Draft));
        await SeedAsync(Row(OrgA, "in-pausa", status: SupplierServiceListingStatus.Paused));
        await SeedAsync(Row(OrgA, "eliminata", deletedAt: Created));
        await SeedAsync(Row(OrgB, "di-un-altro", sortOrder: 0));

        var services = await Catalog().ListPublicAsync(OrgA);

        Assert.Equal(new[] { "prima", "seconda-a", "seconda-b", "terza" }, services.Select(s => s.Slug));
    }

    [Theory]
    [InlineData(SupplierStatus.Pending)]
    [InlineData(SupplierStatus.Suspended)]
    public async Task ListPublicAsync_AndFindPublicAsync_ASupplierThatIsNotActive_HasNoPublicService(SupplierStatus status)
    {
        await SeedSuppliersAsync(statusA: status);
        await SeedAsync(Row(OrgA, "pulizia"));

        Assert.Empty(await Catalog().ListPublicAsync(OrgA));
        Assert.Null(await Catalog().FindPublicAsync(OrgA, "pulizia"));
    }

    [Fact]
    public async Task ListPublicAsync_AnOrgWithNoSupplierProfile_HasNoPublicService()
    {
        // A service row with no profile of an active supplier behind it (the foreign key is not enforced in memory, but the
        // public statement joins the profile): it must not come out.
        await using (var db = CreateDb())
        {
            db.SupplierServiceListings.Add(Row(OrgA, "orfano"));
            await db.SaveChangesAsync();
        }

        Assert.Empty(await Catalog().ListPublicAsync(OrgA));
    }

    [Fact]
    public async Task FindPublicAsync_IsBySlugInTheOrgAsked_AndTheSameSlugOfTwoSuppliersIsTwoServices()
    {
        await SeedSuppliersAsync();
        await SeedAsync(Row(OrgA, "pulizia", price: 1000));
        await SeedAsync(Row(OrgB, "pulizia", price: 2000));

        var a = await Catalog().FindPublicAsync(OrgA, "pulizia");
        var b = await Catalog().FindPublicAsync(OrgB, "pulizia");

        Assert.Equal(1000, a!.PriceFromCents);
        Assert.Equal(2000, b!.PriceFromCents);
        Assert.Null(await Catalog().FindPublicAsync(OrgA, "non-esiste"));
        // The slug is matched as stored (lowercase): the showcase lowercases what the address bar sends.
        Assert.Null(await Catalog().FindPublicAsync(OrgA, "PULIZIA"));
    }

    [Theory]
    [InlineData(SupplierServiceListingStatus.Draft)]
    [InlineData(SupplierServiceListingStatus.Paused)]
    public async Task FindPublicAsync_ADraftAndAPausedService_AreNotThere(SupplierServiceListingStatus status)
    {
        await SeedSuppliersAsync();
        await SeedAsync(Row(OrgA, "pulizia", status: status));

        Assert.Null(await Catalog().FindPublicAsync(OrgA, "pulizia"));
    }

    [Fact]
    public async Task FindPublicAsync_ADeletedService_IsNotThere_AndItsSlugCanBeReusedByANewOne()
    {
        await SeedSuppliersAsync();
        await SeedAsync(Row(OrgA, "pulizia", price: 1000, deletedAt: Created));
        await SeedAsync(Row(OrgA, "pulizia", price: 3000));

        Assert.Equal(3000, (await Catalog().FindPublicAsync(OrgA, "pulizia"))!.PriceFromCents);
    }

    [Fact]
    public void ThePublicService_HasNoFieldTheConsoleKeepsToItself()
    {
        var names = typeof(SupplierPublicService).GetProperties().Select(p => p.Name).ToList();

        foreach (var field in new[] { "Id", "OrgId", "Status", "Version", "SortOrder", "CreatedAt", "UpdatedAt", "DeletedAt", "SupplierProfile" })
            Assert.DoesNotContain(field, names);
    }

    private SupplierServiceCatalogService Catalog() =>
        new(CreateDb(), Mock.Of<IFileStorage>(), Mock.Of<IImageStorageService>(), NullLogger<SupplierServiceCatalogService>.Instance);

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private async Task SeedSuppliersAsync(SupplierStatus statusA = SupplierStatus.Active)
    {
        await using var db = CreateDb();
        db.SupplierProfiles.AddRange(Profile(OrgA, statusA), Profile(OrgB, SupplierStatus.Active));
        await db.SaveChangesAsync();
    }

    private async Task SeedAsync(SupplierServiceListing listing)
    {
        await using var db = CreateDb();
        db.SupplierServiceListings.Add(listing);
        await db.SaveChangesAsync();
    }

    private static SupplierProfile Profile(Guid orgId, SupplierStatus status) =>
        new()
        {
            OrgId = orgId,
            Email = $"{orgId:N}@example.com",
            LegalName = "Fornitore",
            Phone = "+39 06 000000",
            Status = status,
        };

    private static SupplierServiceListing Row(
        Guid orgId,
        string slug,
        int sortOrder = 0,
        DateTime? created = null,
        SupplierServiceListingStatus status = SupplierServiceListingStatus.Active,
        DateTime? deletedAt = null,
        int? price = 1000) =>
        new()
        {
            OrgId = orgId,
            Slug = slug,
            Name = $"Servizio {slug}",
            Category = ServiceCategories.Cleaning,
            PriceFromCents = price,
            DurationMinutes = 60,
            SortOrder = sortOrder,
            Status = status,
            DeletedAt = deletedAt,
            CreatedAt = created ?? Created,
            UpdatedAt = created ?? Created,
        };
}
