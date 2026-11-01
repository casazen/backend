using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyListingTextServiceTests
{
    private const string ComoCode = "013075";
    private const string Answer =
        "{\"title\": \"Bilocale luminoso a Como\", \"description\": \"Un appartamento luminoso.\\n\\nCome sono gli spazi.\"}";

    private static readonly Guid HostOrg = Guid.NewGuid();
    private static readonly Guid OtherOrg = Guid.NewGuid();
    private static readonly AiImage Photo = new("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0]);

    private readonly Mock<IAiProvider> _provider = new();
    private readonly List<AiRequest> _requests = [];

    public PropertyListingTextServiceTests()
    {
        _provider
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, AiModelTier, string, CancellationToken>((request, _, _, _) => _requests.Add(request))
            .ReturnsAsync((AiRequest request, AiModelTier tier, string _, CancellationToken _) =>
                new AiGenerationResult(Answer, 900, 200, tier, false, ImagesRead: request.Images.Count));
    }

    [Fact]
    public async Task SuggestAsync_TheAiAnswers_ReturnsItsTextAndWhatItWasBasedOn()
    {
        await using var db = Database(nameof(SuggestAsync_TheAiAnswers_ReturnsItsTextAndWhatItWasBasedOn));
        var property = await SeedAsync(db);

        var suggestion = await Service(db).SuggestAsync(property, [Photo, Photo], regenerate: false);

        Assert.True(suggestion.AiUsed);
        Assert.Null(suggestion.Reason);
        Assert.Equal("Bilocale luminoso a Como", suggestion.Title);
        Assert.Equal("Un appartamento luminoso.\n\nCome sono gli spazi.", suggestion.Description);
        Assert.Equal(new ListingTextBasis(2, "Como", "CO", "Lombardia", 2, 2), suggestion.BasedOn);

        var request = Assert.Single(_requests);
        Assert.Equal(2, request.Images.Count);
        Assert.Equal(ListingTextPrompt.SystemPrompt, request.SystemPrompt);
        Assert.DoesNotContain("Via Borgovico", request.Prompt);
        Assert.DoesNotContain("Casa Rossi", request.Prompt);
    }

    [Fact]
    public async Task SuggestAsync_SimilarListings_PublishedInTheSameComuneOfAnyOrgOnly()
    {
        await using var db = Database(nameof(SuggestAsync_SimilarListings_PublishedInTheSameComuneOfAnyOrgOnly));
        var property = await SeedAsync(db);

        await Service(db).SuggestAsync(property, [], regenerate: false);

        var prompt = Assert.Single(_requests).Prompt;
        Assert.Contains("«Trilocale della stessa org»", prompt);
        Assert.Contains("«Bilocale di un'altra org»", prompt);
        Assert.DoesNotContain("Non pubblicato", prompt);
        Assert.DoesNotContain("Altro comune", prompt);
        Assert.DoesNotContain("Org disattivata", prompt);
        Assert.DoesNotContain("Bilocale luminoso", prompt);
        // The same kind of place first, then the closest number of guests.
        Assert.True(prompt.IndexOf("«Bilocale di un'altra org»", StringComparison.Ordinal)
            < prompt.IndexOf("«Trilocale della stessa org»", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuggestAsync_SameDataAgain_ReusesTheTextUnlessARegenerationIsAsked()
    {
        await using var db = Database(nameof(SuggestAsync_SameDataAgain_ReusesTheTextUnlessARegenerationIsAsked));
        var property = await SeedAsync(db);
        var service = Service(db);

        await service.SuggestAsync(property, [Photo], regenerate: false);
        var again = await service.SuggestAsync(property, [Photo], regenerate: false);
        await service.SuggestAsync(property, [Photo], regenerate: true);

        Assert.True(again.AiUsed);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task SuggestAsync_NoProvider_SaysSoAndCachesNothing()
    {
        await using var db = Database(nameof(SuggestAsync_NoProvider_SaysSoAndCachesNothing));
        var property = await SeedAsync(db);
        _provider
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("placeholder", 0, 0, AiModelTier.Economy, false, ProviderConfigured: false));
        var service = Service(db);

        var first = await service.SuggestAsync(property, [Photo], regenerate: false);
        await service.SuggestAsync(property, [Photo], regenerate: false);

        Assert.False(first.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiNotConfigured, first.Reason);
        Assert.Null(first.Title);
        Assert.Equal(0, first.BasedOn.Photos);
        _provider.Verify(
            p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task SuggestAsync_BudgetUsedUp_FallsBack()
    {
        await using var db = Database(nameof(SuggestAsync_BudgetUsedUp_FallsBack));
        var property = await SeedAsync(db);
        _provider
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        var suggestion = await Service(db).SuggestAsync(property, [], regenerate: false);

        Assert.False(suggestion.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiBudgetExhausted, suggestion.Reason);
    }

    [Fact]
    public async Task SuggestAsync_ProviderFails_FallsBack()
    {
        await using var db = Database(nameof(SuggestAsync_ProviderFails_FallsBack));
        var property = await SeedAsync(db);
        _provider
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down", null, System.Net.HttpStatusCode.BadGateway));

        var suggestion = await Service(db).SuggestAsync(property, [], regenerate: false);

        Assert.Equal(AiSuggestionFallbackReasons.AiUnavailable, suggestion.Reason);
    }

    [Fact]
    public async Task SuggestAsync_AnswerIsNotTheJsonAskedFor_FallsBack()
    {
        await using var db = Database(nameof(SuggestAsync_AnswerIsNotTheJsonAskedFor_FallsBack));
        var property = await SeedAsync(db);
        _provider
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("Ecco una bella descrizione senza JSON.", 10, 10, AiModelTier.Standard, false));

        var suggestion = await Service(db).SuggestAsync(property, [], regenerate: false);

        Assert.Equal(AiSuggestionFallbackReasons.AiInvalidAnswer, suggestion.Reason);
    }

    private PropertyListingTextService Service(AppDbContext db)
    {
        var comuni = new Mock<IComuneDirectory>();
        comuni
            .Setup(c => c.FindByIstatCodeAsync(ComoCode, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Comune { IstatCode = ComoCode, Name = "Como", ProvinceCode = "CO", RegionName = "Lombardia" });
        return new PropertyListingTextService(
            db,
            comuni.Object,
            _provider.Object,
            new AiResponseCache(Options.Create(new AiCacheOptions())),
            NullLogger<PropertyListingTextService>.Instance);
    }

    /// <summary>The caller's org scope, as in a request: the listings of the other orgs must still be read.</summary>
    private static AppDbContext Database(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options, new TenantScope(HostOrg));

    private static async Task<Property> SeedAsync(AppDbContext db)
    {
        db.Orgs.AddRange(
            new OrgEntity { Id = HostOrg, Name = "Host", Slug = "host", IsActive = true },
            new OrgEntity { Id = OtherOrg, Name = "Altra", Slug = "altra", IsActive = true });
        var inactiveOrg = new OrgEntity { Id = Guid.NewGuid(), Name = "Spenta", Slug = "spenta", IsActive = false };
        db.Orgs.Add(inactiveOrg);

        var property = Listing(HostOrg, "Bilocale luminoso", AccommodationType.Apartment, 3);
        property.Name = "Casa Rossi";
        property.Address = "Via Borgovico 12";
        property.ComplianceStatus = PropertyComplianceStatus.Pending;
        property.Amenities = [PropertyAmenity.WiFi, PropertyAmenity.AirConditioning];
        db.Properties.AddRange(
            property,
            Listing(HostOrg, "Trilocale della stessa org", AccommodationType.HouseOrVilla, 3),
            Listing(OtherOrg, "Bilocale di un'altra org", AccommodationType.Apartment, 8),
            Listing(OtherOrg, "Non pubblicato", AccommodationType.Apartment, 3, published: false),
            Listing(OtherOrg, "Altro comune", AccommodationType.Apartment, 3, comune: "015146"),
            Listing(inactiveOrg.Id, "Org disattivata", AccommodationType.Apartment, 3));
        await db.SaveChangesAsync();
        return property;
    }

    private static Property Listing(
        Guid orgId, string title, AccommodationType type, int guests, bool published = true, string comune = ComoCode) => new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = title,
            ListingTitle = title,
            Address = "Via Test 1",
            City = "Como",
            ComuneIstatCode = comune,
            AccommodationType = type,
            MaxGuests = guests,
            Bedrooms = 1,
            Bathrooms = 1,
            RentalMode = RentalMode.Short,
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            PublishedOnSite = published,
        };

    private sealed class TenantScope(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
