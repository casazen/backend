using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Pricing;
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

/// <summary>«Calcola con l'AI» of the step «Prezzi» (WIZ-AI-PRICE): what reaches the AI, what is believed of its answer, what the host gets.</summary>
public class ListingPriceSuggestionServiceTests
{
    private const string ComoIstat = "013075";

    private static readonly Comune Como = new()
    {
        IstatCode = ComoIstat,
        Name = "Como",
        DisplayName = "Como",
        ProvinceCode = "CO",
        RegionName = "Lombardia",
    };

    private const string GoodAnswer =
        """{"nightlyRate": 135, "rangeMin": 115, "rangeMax": 160, "cleaningFee": 60, "weekendSurchargePercent": 15, "minNights": 2, "rationale": "Zona centrale e terrazza."}""";

    // ─── What the host gets ──────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_TheAiAnswers_ReturnsTheCheckedPriceAndWhatItWasBasedOn()
    {
        await using var db = CreateDb();
        var (orgId, property) = await SeedHomeAsync(db);
        await SeedOtherHomesAsync(db, 100m, 120m, 140m, 160m);
        var ai = ProviderAnswering(GoodAnswer);

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.True(suggestion.AiUsed);
        Assert.Null(suggestion.Reason);
        Assert.Equal(135m, suggestion.NightlyRate);
        Assert.Equal(115m, suggestion.RangeMin);
        Assert.Equal(160m, suggestion.RangeMax);
        Assert.Equal(60m, suggestion.CleaningFee);
        Assert.Equal(15m, suggestion.WeekendSurchargePercent);
        Assert.Equal(2, suggestion.MinNights);
        Assert.Equal("Zona centrale e terrazza.", suggestion.Rationale);

        Assert.Equal("Como", suggestion.BasedOn.Comune);
        Assert.Equal("CO", suggestion.BasedOn.Province);
        Assert.Equal("Lombardia", suggestion.BasedOn.Region);
        Assert.Equal(4, suggestion.BasedOn.SimilarListings);
        // The wifi, the terrace and the hot tub: the smoke detector says nothing about a price.
        Assert.Equal(3, suggestion.BasedOn.Amenities);
        Assert.Equal(
            new[]
            {
                PriceSuggestionFields.Type,
                PriceSuggestionFields.Guests,
                PriceSuggestionFields.Bedrooms,
                PriceSuggestionFields.Bathrooms,
                PriceSuggestionFields.Area,
                PriceSuggestionFields.Amenities,
            },
            suggestion.BasedOn.UsedFields);
        Assert.NotEqual(Guid.Empty, orgId);
    }

    [Fact]
    public async Task SuggestAsync_ThePrompt_CarriesTheFactsAndTheFiguresButNothingTheHostTyped()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        await SeedOtherHomesAsync(db, 100m, 120m, 140m, 160m);
        var ai = ProviderAnswering(GoodAnswer);

        await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        var prompt = SentPrompt(ai);
        Assert.Contains("Como (CO), Lombardia", prompt);
        Assert.Contains("Ospiti: 3", prompt);
        Assert.Contains("Superficie: 75 m²", prompt);
        Assert.Contains("terrazza", prompt);
        Assert.Contains("vasca idromassaggio", prompt);
        Assert.Contains("mediana 130 €", prompt);
        Assert.Contains("in linea con le case simili", prompt);
        // What a host types in a free field never leaves, and neither does anything else that identifies the home.
        Assert.DoesNotContain("Casa Como", prompt);
        Assert.DoesNotContain("Via Natta", prompt);
        Assert.DoesNotContain("Il segreto della casa", prompt);
        Assert.DoesNotContain("detector", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PricePositioning.Lower, "più economico")]
    [InlineData(PricePositioning.Higher, "più alto")]
    public async Task SuggestAsync_ThePositioning_IsToldToTheAi(PricePositioning positioning, string expected)
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = ProviderAnswering(GoodAnswer);

        await CreateService(db, ai.Object).SuggestAsync(property, positioning, "it");

        Assert.Contains(expected, SentPrompt(ai));
    }

    [Fact]
    public async Task SuggestAsync_AnEnglishRequest_AsksForAnEnglishExplanation()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = ProviderAnswering(GoodAnswer);

        await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "en");

        Assert.Contains("in inglese", SentPrompt(ai));
    }

    // ─── The similar homes ───────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_HomesThatTheSiteDoesNotShow_AndTheHostsOwn_AreNotInTheFigures()
    {
        await using var db = CreateDb();
        var (orgId, property) = await SeedHomeAsync(db);
        var otherOrgId = await AddOrgAsync(db);
        var inactiveOrgId = await AddOrgAsync(db, isActive: false);

        // Two homes that count, and then everything that must not: the figures stay under the minimum.
        await AddHomeAsync(db, otherOrgId, 100m);
        await AddHomeAsync(db, otherOrgId, 120m);
        await AddHomeAsync(db, orgId, 900m);
        await AddHomeAsync(db, otherOrgId, 50m, home => home.IsPaused = true);
        await AddHomeAsync(db, otherOrgId, 60m, home => home.ComplianceStatus = PropertyComplianceStatus.Pending);
        await AddHomeAsync(db, otherOrgId, 70m, home => home.PublishedOnSite = false);
        await AddHomeAsync(db, otherOrgId, 80m, home => home.IsTestData = true);
        await AddHomeAsync(db, otherOrgId, 0m);
        await AddHomeAsync(db, otherOrgId, 90m, home => home.ComuneIstatCode = "015146");
        await AddHomeAsync(db, otherOrgId, 95m, home => home.RentalMode = RentalMode.Long);
        await AddHomeAsync(db, inactiveOrgId, 110m);
        var ai = ProviderAnswering(GoodAnswer);

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.Equal(0, suggestion.BasedOn.SimilarListings);
        Assert.Contains("non ci sono ancora abbastanza case pubblicate a Como", SentPrompt(ai));
        Assert.DoesNotContain("mediana", SentPrompt(ai));
    }

    [Fact]
    public async Task SuggestAsync_HomesOfASimilarSize_AreComparedFirst()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var otherOrgId = await AddOrgAsync(db);
        foreach (var rate in new[] { 100m, 110m, 120m })
            await AddHomeAsync(db, otherOrgId, rate);
        foreach (var rate in new[] { 400m, 450m, 500m })
            await AddHomeAsync(db, otherOrgId, rate, home => { home.Bedrooms = 5; home.MaxGuests = 10; });
        var ai = ProviderAnswering(GoodAnswer);

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.Equal(3, suggestion.BasedOn.SimilarListings);
        var prompt = SentPrompt(ai);
        Assert.Contains("di dimensioni simili a questa", prompt);
        Assert.DoesNotContain("450", prompt);
    }

    [Fact]
    public async Task SuggestAsync_TooFewHomesOfASimilarSize_FallBackToEveryHomeOfTheComune()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var otherOrgId = await AddOrgAsync(db);
        foreach (var rate in new[] { 100m, 110m })
            await AddHomeAsync(db, otherOrgId, rate);
        foreach (var rate in new[] { 400m, 450m, 500m })
            await AddHomeAsync(db, otherOrgId, rate, home => { home.Bedrooms = 5; home.MaxGuests = 10; });
        var ai = ProviderAnswering("""{"nightlyRate": 300, "rationale": "Ok"}""");

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.Equal(5, suggestion.BasedOn.SimilarListings);
        Assert.Contains("di varie dimensioni", SentPrompt(ai));
        // Homes of every size are no band: a price over all the figures of the small ones is not refused.
        Assert.True(suggestion.AiUsed);
        Assert.Equal(300m, suggestion.NightlyRate);
    }

    // ─── Without the AI ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_NoProviderConfigured_AndNoFigures_SaysWhyAndSuggestsNothing()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = ProviderAnswering("<article>contenuto di prova</article>", providerConfigured: false);

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.False(suggestion.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiNotConfigured, suggestion.Reason);
        Assert.Null(suggestion.NightlyRate);
        Assert.Null(suggestion.Rationale);
        Assert.Equal("Como", suggestion.BasedOn.Comune);
    }

    [Theory]
    [InlineData(PricePositioning.Lower, 115)]
    [InlineData(PricePositioning.Market, 130)]
    [InlineData(PricePositioning.Higher, 145)]
    public async Task SuggestAsync_NoProviderConfigured_ButFigures_PricesFromTheFiguresAlone(PricePositioning positioning, double expected)
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        await SeedOtherHomesAsync(db, 100m, 120m, 140m, 160m);
        var ai = ProviderAnswering(string.Empty, providerConfigured: false);

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, positioning, "it");

        Assert.False(suggestion.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiNotConfigured, suggestion.Reason);
        Assert.Equal((decimal)expected, suggestion.NightlyRate);
        Assert.Null(suggestion.CleaningFee);
        Assert.Null(suggestion.Rationale);
        Assert.Equal(4, suggestion.BasedOn.SimilarListings);
    }

    [Fact]
    public async Task SuggestAsync_TheBudgetIsGone_SaysSo()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = new Mock<IAiProvider>();
        ai.Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.False(suggestion.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiBudgetExhausted, suggestion.Reason);
    }

    [Fact]
    public async Task SuggestAsync_TheProviderFails_IsNotAnErrorForTheHost()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = new Mock<IAiProvider>();
        ai.Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("502"));

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.False(suggestion.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiUnavailable, suggestion.Reason);
    }

    [Fact]
    public async Task SuggestAsync_TheProviderTimesOut_IsTheProvidersFailureNotTheCallersLeaving()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = new Mock<IAiProvider>();
        ai.Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var suggestion = await CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.Equal(PriceSuggestionFallbackReasons.AiUnavailable, suggestion.Reason);
    }

    [Fact]
    public async Task SuggestAsync_TheCallerLeaves_TheRequestIsCancelled()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        using var cancellation = new CancellationTokenSource();
        var ai = new Mock<IAiProvider>();
        // The caller leaves while the provider is answering: the call is cancelled, not turned into «the AI is not available».
        ai.Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, AiModelTier _, string _, CancellationToken _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(db, ai.Object).SuggestAsync(property, PricePositioning.Market, "it", cancellation.Token));
    }

    [Theory]
    [InlineData("Non saprei che prezzo suggerire.")]
    [InlineData("""{"rangeMin": 100, "rangeMax": 200}""")]
    [InlineData("""{"nightlyRate": 3}""")]
    public async Task SuggestAsync_AnAnswerThatIsNotAPrice_IsRefused(string answer)
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);

        var suggestion = await CreateService(db, ProviderAnswering(answer).Object).SuggestAsync(property, PricePositioning.Market, "it");

        Assert.False(suggestion.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiInvalidAnswer, suggestion.Reason);
        Assert.Null(suggestion.NightlyRate);
    }

    [Fact]
    public async Task SuggestAsync_APriceFarFromTheSimilarHomes_FallsBackToTheirFigures()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        await SeedOtherHomesAsync(db, 100m, 120m, 140m, 160m);

        var suggestion = await CreateService(db, ProviderAnswering("""{"nightlyRate": 900, "rationale": "Lusso."}""").Object)
            .SuggestAsync(property, PricePositioning.Market, "it");

        Assert.False(suggestion.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiInvalidAnswer, suggestion.Reason);
        Assert.Equal(130m, suggestion.NightlyRate);
        Assert.Null(suggestion.Rationale);
    }

    // ─── The cache ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_TheSameDataTwice_AsksTheAiOnce()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = ProviderAnswering(GoodAnswer);
        var service = CreateService(db, ai.Object);

        var first = await service.SuggestAsync(property, PricePositioning.Market, "it");
        var second = await service.SuggestAsync(property, PricePositioning.Market, "it");

        Assert.Equal(first.NightlyRate, second.NightlyRate);
        Assert.True(second.AiUsed);
        VerifyCalls(ai, Times.Once());
    }

    [Fact]
    public async Task SuggestAsync_AnotherPositioningOrLanguage_AsksAgain()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = ProviderAnswering(GoodAnswer);
        var service = CreateService(db, ai.Object);

        await service.SuggestAsync(property, PricePositioning.Market, "it");
        await service.SuggestAsync(property, PricePositioning.Higher, "it");
        await service.SuggestAsync(property, PricePositioning.Market, "en");

        VerifyCalls(ai, Times.Exactly(3));
    }

    [Fact]
    public async Task SuggestAsync_ABadAnswer_IsNotKeptForTheNextTime()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var ai = ProviderAnswering("Non saprei.");
        var service = CreateService(db, ai.Object);

        await service.SuggestAsync(property, PricePositioning.Market, "it");
        await service.SuggestAsync(property, PricePositioning.Market, "it");

        VerifyCalls(ai, Times.Exactly(2));
    }

    [Fact]
    public async Task SuggestAsync_TheAnswerOfAnotherOrg_IsNeverReused()
    {
        await using var db = CreateDb();
        var (_, property) = await SeedHomeAsync(db);
        var other = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            OwnerId = "owner-2",
            Name = property.Name,
            City = property.City,
            ComuneIstatCode = property.ComuneIstatCode,
            Bedrooms = property.Bedrooms,
            Bathrooms = property.Bathrooms,
            MaxGuests = property.MaxGuests,
            AreaSqm = property.AreaSqm,
            AccommodationType = property.AccommodationType,
            Amenities = [.. property.Amenities],
        };
        var ai = ProviderAnswering(GoodAnswer);
        var service = CreateService(db, ai.Object);

        await service.SuggestAsync(property, PricePositioning.Market, "it");
        await service.SuggestAsync(other, PricePositioning.Market, "it");

        VerifyCalls(ai, Times.Exactly(2));
    }

    // ─── Is the AI there ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AiOptions.StubProvider, null, null, false)]
    [InlineData("", "key", null, false)]
    [InlineData(AiOptions.OpenAiProvider, null, null, false)]
    [InlineData(AiOptions.CompatibleProvider, "key", "", false)]
    [InlineData(AiOptions.OpenAiProvider, "key", "https://api.openai.com/v1", true)]
    [InlineData(AiOptions.CompatibleProvider, "key", "https://ai.example/v1", true)]
    public async Task IsAvailableAsync_OnlyAnExternalProviderWithAKeyIsAnAi(string provider, string? apiKey, string? baseUrl, bool expected)
    {
        await using var db = CreateDb();
        var settings = new Mock<IAiRuntimeSettings>();
        settings
            .Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiOptions { Provider = provider, ApiKey = apiKey, OpenAiBaseUrl = baseUrl! });

        var available = await CreateService(db, ProviderAnswering(GoodAnswer).Object, settings.Object).IsAvailableAsync();

        Assert.Equal(expected, available);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ListingPriceSuggestionService CreateService(AppDbContext db, IAiProvider ai, IAiRuntimeSettings? settings = null)
    {
        var directory = new Mock<IComuneDirectory>();
        directory
            .Setup(d => d.FindByIstatCodeAsync(ComoIstat, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Como);
        return new ListingPriceSuggestionService(
            db,
            ai,
            new AiResponseCache(Options.Create(new AiCacheOptions())),
            settings ?? Mock.Of<IAiRuntimeSettings>(),
            directory.Object,
            NullLogger<ListingPriceSuggestionService>.Instance);
    }

    private static Mock<IAiProvider> ProviderAnswering(string content, bool providerConfigured = true)
    {
        var ai = new Mock<IAiProvider>();
        ai.Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(content, 120, 60, AiModelTier.Economy, FromCache: false, ProviderConfigured: providerConfigured));
        return ai;
    }

    private static string SentPrompt(Mock<IAiProvider> ai) =>
        Assert.Single(ai.Invocations, invocation => invocation.Method.Name == nameof(IAiProvider.GenerateAsync))
            .Arguments[0]
            .ToString()!;

    private static void VerifyCalls(Mock<IAiProvider> ai, Times times) =>
        ai.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            times);

    /// <summary>The host's home: a flat in Como, 3 guests, 1 bedroom, 2 bathrooms, 75 m², with a terrace and a hot tub.</summary>
    private static async Task<(Guid OrgId, Property Property)> SeedHomeAsync(AppDbContext db)
    {
        var orgId = await AddOrgAsync(db);
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "owner-1",
            Name = "Casa Como",
            Address = "Via Natta 17",
            City = "Como",
            ComuneIstatCode = ComoIstat,
            Description = "Il segreto della casa",
            Bedrooms = 1,
            Bathrooms = 2,
            MaxGuests = 3,
            AreaSqm = 75,
            AccommodationType = AccommodationType.Apartment,
            Amenities = [PropertyAmenity.WiFi, PropertyAmenity.Terrace, PropertyAmenity.HotTub, PropertyAmenity.SmokeDetector],
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (orgId, property);
    }

    private static async Task SeedOtherHomesAsync(AppDbContext db, params decimal[] rates)
    {
        var otherOrgId = await AddOrgAsync(db);
        foreach (var rate in rates)
            await AddHomeAsync(db, otherOrgId, rate);
    }

    private static async Task<Guid> AddOrgAsync(AppDbContext db, bool isActive = true)
    {
        var id = Guid.NewGuid();
        db.Orgs.Add(new Casazen.Core.Entities.Org
        {
            Id = id,
            Name = $"Org {id:N}",
            Slug = $"org-{id:N}",
            DisplayName = "Host",
            ContactEmail = "host@example.com",
            IsActive = isActive,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>A home the public site shows: short rent, active, activated, 1 bedroom, 3 guests, in Como; <paramref name="change"/> spoils it.</summary>
    private static async Task AddHomeAsync(AppDbContext db, Guid orgId, decimal rate, Action<Property>? change = null)
    {
        var home = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "owner-x",
            Name = "Altra casa",
            City = "Como",
            ComuneIstatCode = ComoIstat,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 3,
            NightlyRate = rate,
            RentalMode = RentalMode.Short,
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            PublishedOnSite = true,
        };
        change?.Invoke(home);
        db.Properties.Add(home);
        await db.SaveChangesAsync();
    }
}
