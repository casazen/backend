using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.Supplier;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-02: the mapping between the catalog's entity, its response DTO and the request that creates or replaces it.</summary>
public class SupplierServiceMapperTests
{
    private static readonly DateTime Created = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Updated = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    private static SupplierServiceListing Complete() =>
        new()
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            OrgId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Slug = "pulizia-cambio-ospiti",
            Name = "Pulizia cambio ospiti",
            Category = ServiceCategories.Cleaning,
            Summary = "Breve",
            Description = "Lunga",
            PriceFromCents = 4500,
            PriceUnit = SupplierServicePriceUnit.PerHour,
            PricesIncludeVat = true,
            RequiresQuote = false,
            DurationMinutes = 120,
            MinNoticeHours = 12,
            WeekdaysMask = 0b1100101,
            SupplementsJson = """[{"code":"bagno","label":"Bagno","amountCents":1000,"per":"bathroom","max":3},{"code":"set","label":"Set","amountCents":1200,"per":"set"}]""",
            IncludedJson = """["Biancheria"]""",
            ExcludedJson = """["Tende","Vetri"]""",
            PhotoUrlsJson = """["https://s/a.jpg","https://s/b.jpg"]""",
            Status = SupplierServiceListingStatus.Active,
            SortOrder = 3,
            CreatedAt = Created,
            UpdatedAt = Updated,
            Version = 4242,
        };

    [Fact]
    public void ToDto_MapsEveryFieldOfACompleteService()
    {
        var dto = SupplierServiceMapper.ToDto(Complete());

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), dto.Id);
        Assert.Equal("pulizia-cambio-ospiti", dto.Slug);
        Assert.Equal("Pulizia cambio ospiti", dto.Name);
        Assert.Equal("cleaning", dto.Category);
        Assert.Equal("Breve", dto.Summary);
        Assert.Equal("Lunga", dto.Description);
        Assert.Equal(4500, dto.PriceFromCents);
        Assert.Equal(SupplierServicePriceUnit.PerHour, dto.PriceUnit);
        Assert.True(dto.PricesIncludeVat);
        Assert.False(dto.RequiresQuote);
        Assert.Equal(120, dto.DurationMinutes);
        Assert.Equal(12, dto.MinNoticeHours);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday }, dto.Weekdays);
        Assert.Equal(new[] { "Biancheria" }, dto.Included);
        Assert.Equal(new[] { "Tende", "Vetri" }, dto.Excluded);
        Assert.Equal(new[] { "https://s/a.jpg", "https://s/b.jpg" }, dto.PhotoUrls);
        Assert.Equal(SupplierServiceListingStatus.Active, dto.Status);
        Assert.Equal(3, dto.SortOrder);
        Assert.True(dto.Publishable);
        Assert.Empty(dto.MissingForPublication);
        Assert.Equal(Created, dto.CreatedAt);
        Assert.Equal(Updated, dto.UpdatedAt);
        Assert.Equal(4242u, dto.Version);

        Assert.Equal(2, dto.Supplements.Count);
        Assert.Equal("bagno", dto.Supplements[0].Code);
        Assert.Equal("Bagno", dto.Supplements[0].Label);
        Assert.Equal(1000, dto.Supplements[0].AmountCents);
        Assert.Equal("bathroom", dto.Supplements[0].Per);
        Assert.Equal(3, dto.Supplements[0].Max);
        Assert.Null(dto.Supplements[1].Max);
    }

    [Fact]
    public void ToDto_AnIncompleteDraft_IsNotPublishableAndSaysWhatIsMissing()
    {
        var draft = new SupplierServiceListing { Name = "Bozza", Category = ServiceCategories.Cleaning };

        var dto = SupplierServiceMapper.ToDto(draft);

        Assert.False(dto.Publishable);
        Assert.Equal(new[] { "durationMinutes", "priceFromCents" }, dto.MissingForPublication);
        Assert.Equal(SupplierServiceListingStatus.Draft, dto.Status);
        Assert.Equal(7, dto.Weekdays.Count);
        Assert.Empty(dto.Supplements);
        Assert.Empty(dto.PhotoUrls);
        Assert.False(dto.PricesIncludeVat);
    }

    [Fact]
    public void ToDto_AQuoteInPlaceOfAPrice_IsPublishable()
    {
        var listing = Complete();
        listing.PriceFromCents = null;
        listing.RequiresQuote = true;

        var dto = SupplierServiceMapper.ToDto(listing);

        Assert.True(dto.Publishable);
        Assert.Null(dto.PriceFromCents);
        Assert.True(dto.RequiresQuote);
    }

    [Fact]
    public void ToDto_UnreadableJsonColumns_GiveEmptyListsInsteadOfFailingTheRead()
    {
        var listing = Complete();
        listing.SupplementsJson = "not json";
        listing.IncludedJson = "{}";
        listing.ExcludedJson = "";
        listing.PhotoUrlsJson = "[1]";

        var dto = SupplierServiceMapper.ToDto(listing);

        Assert.Empty(dto.Supplements);
        Assert.Empty(dto.Included);
        Assert.Empty(dto.Excluded);
        Assert.Empty(dto.PhotoUrls);
    }

    [Fact]
    public void ToDto_WeekdaysMaskZero_IsNoDay()
    {
        var listing = Complete();
        listing.WeekdaysMask = 0;

        Assert.Empty(SupplierServiceMapper.ToDto(listing).Weekdays);
    }

    [Fact]
    public void ToDto_SerializedLikeTheApi_HasExactlyTheDocumentedKeysAndEnumNames()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(SupplierServiceMapper.ToDto(Complete()), options));
        var root = json.RootElement;

        Assert.Equal(
            new[]
            {
                "id", "slug", "name", "category", "summary", "description", "priceFromCents", "priceUnit", "pricesIncludeVat",
                "requiresQuote", "durationMinutes", "minNoticeHours", "weekdays", "supplements", "included", "excluded",
                "photoUrls", "status", "sortOrder", "publishable", "missingForPublication", "createdAt", "updatedAt", "version",
            }.Order(),
            root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("PerHour", root.GetProperty("priceUnit").GetString());
        Assert.Equal("Active", root.GetProperty("status").GetString());
        Assert.Equal(
            new[] { "Monday", "Wednesday", "Saturday", "Sunday" },
            root.GetProperty("weekdays").EnumerateArray().Select(e => e.GetString()));
        var supplement = root.GetProperty("supplements")[0];
        Assert.Equal(
            new[] { "code", "label", "amountCents", "per", "max" }.Order(),
            supplement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public void ToInput_MapsTheRequestAsItIs_NothingIsCheckedYet()
    {
        var request = new UpdateSupplierServiceRequest
        {
            Name = " Pulizia ",
            Category = "Cleaning",
            Summary = "Breve",
            Description = "Lunga",
            PriceFromCents = 4500,
            PriceUnit = SupplierServicePriceUnit.PerSquareMeter,
            PricesIncludeVat = true,
            RequiresQuote = true,
            DurationMinutes = 90,
            MinNoticeHours = 0,
            Weekdays = [DayOfWeek.Friday],
            Supplements =
            [
                new SupplierServiceSupplementDto { Code = "Extra", Label = "Extra", AmountCents = 500, Per = "flat", Max = 2 },
                null,
            ],
            Included = ["Uno", null],
            Excluded = ["Due"],
            PhotoUrls = ["https://s/a.jpg"],
            SortOrder = 7,
            Version = 9,
        };

        var input = SupplierServiceMapper.ToInput(request);

        Assert.Equal(" Pulizia ", input.Name);
        Assert.Equal("Cleaning", input.Category);
        Assert.Equal("Breve", input.Summary);
        Assert.Equal("Lunga", input.Description);
        Assert.Equal(4500, input.PriceFromCents);
        Assert.Equal(SupplierServicePriceUnit.PerSquareMeter, input.PriceUnit);
        Assert.True(input.PricesIncludeVat);
        Assert.True(input.RequiresQuote);
        Assert.Equal(90, input.DurationMinutes);
        Assert.Equal(0, input.MinNoticeHours);
        Assert.Equal(new[] { DayOfWeek.Friday }, input.Weekdays);
        Assert.Equal(new SupplierServiceSupplement("Extra", "Extra", 500, "flat", 2), input.Supplements![0]);
        Assert.Null(input.Supplements[1]);
        Assert.Equal(new string?[] { "Uno", null }, input.Included);
        Assert.Equal(new[] { "Due" }, input.Excluded);
        Assert.Equal(new[] { "https://s/a.jpg" }, input.PhotoUrls);
        Assert.Equal(7, input.SortOrder);
    }

    [Fact]
    public void ToInput_OnlyNameAndCategory_LeavesTheRestToTheDefaultsOfTheRules()
    {
        var input = SupplierServiceMapper.ToInput(new SaveSupplierServiceRequest { Name = "Pulizia", Category = "cleaning" });

        Assert.Equal(SupplierServicePriceUnit.PerJob, input.PriceUnit);
        Assert.False(input.PricesIncludeVat);
        Assert.False(input.RequiresQuote);
        Assert.Null(input.PriceFromCents);
        Assert.Null(input.DurationMinutes);
        Assert.Null(input.Weekdays);
        Assert.Null(input.Supplements);
        Assert.Null(input.Included);
        Assert.Null(input.PhotoUrls);
        Assert.Null(input.SortOrder);

        var content = SupplierServiceListingRules.Normalize(input);
        Assert.Equal(SupplierServiceWeekdays.AllMask, content.WeekdaysMask);
        Assert.Null(content.PhotoUrls);
    }
}
