using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Pricing;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>The prompt of the nightly price (WIZ-AI-PRICE): what it carries, and what it never does.</summary>
public class ListingPriceSuggestionPromptTests
{
    private static readonly MarketPriceStats Market = new(4, 100m, 115m, 130m, 145m, 160m, SimilarSize: true);

    private static Property Home(Action<Property>? change = null)
    {
        var property = new Property
        {
            Name = "Casa Como",
            Address = "Via Natta 17",
            City = "Como",
            Description = "Descrizione scritta dal proprietario",
            HouseRules = "Niente feste",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            Amenities = [PropertyAmenity.Pool, PropertyAmenity.SmokeDetector, PropertyAmenity.FireExtinguisher, PropertyAmenity.PetFriendly],
        };
        change?.Invoke(property);
        return property;
    }

    [Fact]
    public void FactsOf_LeavesOutTheAmenitiesThatSayNothingAboutAPrice()
    {
        var facts = ListingPriceSuggestionPrompt.FactsOf(
            Home(p => p.Amenities.AddRange([PropertyAmenity.SmokingAllowed, PropertyAmenity.LuggageDropOffAllowed, PropertyAmenity.Pool])),
            comune: null);

        Assert.Equal(new[] { "piscina", "animali ammessi" }, facts.Amenities);
    }

    [Fact]
    public void FactsOf_WithoutAComune_UsesTheCityAndNoProvince()
    {
        var facts = ListingPriceSuggestionPrompt.FactsOf(Home(), comune: null);

        Assert.Equal("Como", facts.Comune);
        Assert.Null(facts.Province);
        Assert.Null(facts.Region);
    }

    [Fact]
    public void FactsOf_TheOfficialComune_WinsOverTheTypedCity()
    {
        var comune = new Comune { IstatCode = "013075", Name = "Como", DisplayName = "Como", ProvinceCode = "CO", RegionName = "Lombardia" };

        var facts = ListingPriceSuggestionPrompt.FactsOf(Home(p => p.City = "como (centro)"), comune);

        Assert.Equal("Como", facts.Comune);
        Assert.Equal("CO", facts.Province);
        Assert.Equal("Lombardia", facts.Region);
    }

    [Fact]
    public void UsedFields_ListsOnlyWhatTheHomeHas()
    {
        var bare = ListingPriceSuggestionPrompt.FactsOf(
            Home(p =>
            {
                p.Amenities.Clear();
                p.Bathrooms = 0;
                p.MaxGuests = 0;
            }),
            comune: null);
        var full = ListingPriceSuggestionPrompt.FactsOf(
            Home(p =>
            {
                p.AccommodationType = AccommodationType.LoftOrPenthouse;
                p.Beds = 3;
                p.AreaSqm = 60;
                p.AreaKind = PropertyAreaKind.Coast;
            }),
            comune: null);

        Assert.Equal(new[] { PriceSuggestionFields.Bedrooms }, ListingPriceSuggestionPrompt.UsedFields(bare));
        Assert.Equal(
            new[]
            {
                PriceSuggestionFields.Type,
                PriceSuggestionFields.Guests,
                PriceSuggestionFields.Bedrooms,
                PriceSuggestionFields.Beds,
                PriceSuggestionFields.Bathrooms,
                PriceSuggestionFields.Area,
                PriceSuggestionFields.Amenities,
                PriceSuggestionFields.Setting,
            },
            ListingPriceSuggestionPrompt.UsedFields(full));
    }

    [Fact]
    public void Build_NeverCarriesAFreeTextOfTheHost()
    {
        var facts = ListingPriceSuggestionPrompt.FactsOf(Home(), comune: null);

        var prompt = ListingPriceSuggestionPrompt.Build(facts, Market, PricePositioning.Market, "it");

        Assert.DoesNotContain("Casa Como", prompt);
        Assert.DoesNotContain("Via Natta", prompt);
        Assert.DoesNotContain("Descrizione scritta", prompt);
        Assert.DoesNotContain("Niente feste", prompt);
    }

    [Fact]
    public void Build_WithoutFigures_TellsTheAiToSayItIsAGeneralEstimate()
    {
        var facts = ListingPriceSuggestionPrompt.FactsOf(Home(), comune: null);

        var prompt = ListingPriceSuggestionPrompt.Build(facts, market: null, PricePositioning.Market, "it");

        Assert.Contains("non ci sono ancora abbastanza case pubblicate a Como", prompt);
        Assert.Contains("stima generale", prompt);
        Assert.DoesNotContain("Dati reali di CasaZen", prompt);
    }

    [Fact]
    public void Build_WithAPlaceItDoesNotKnow_SaysSo()
    {
        var facts = ListingPriceSuggestionPrompt.FactsOf(Home(p => p.City = string.Empty), comune: null);

        var prompt = ListingPriceSuggestionPrompt.Build(facts, market: null, PricePositioning.Market, "it");

        Assert.Contains("Posizione: non indicata", prompt);
        Assert.Contains("in questa zona", prompt);
    }

    [Fact]
    public void Build_AlwaysAsksForOneJsonObject_InTheLanguageOfTheHost()
    {
        var facts = ListingPriceSuggestionPrompt.FactsOf(Home(), comune: null);

        var italian = ListingPriceSuggestionPrompt.Build(facts, Market, PricePositioning.Market, "it");
        var english = ListingPriceSuggestionPrompt.Build(facts, Market, PricePositioning.Market, "en");

        foreach (var prompt in new[] { italian, english })
        {
            Assert.Contains("Rispondi SOLO con un oggetto JSON", prompt);
            Assert.Contains("\"nightlyRate\"", prompt);
            Assert.Contains("\"rationale\"", prompt);
        }

        Assert.Contains("in italiano", italian);
        Assert.Contains("in inglese", english);
        Assert.NotEqual(italian, english);
    }

    [Fact]
    public void Build_IsTheSameForTheSameData()
    {
        var facts = ListingPriceSuggestionPrompt.FactsOf(Home(), comune: null);

        Assert.Equal(
            ListingPriceSuggestionPrompt.Build(facts, Market, PricePositioning.Higher, "it"),
            ListingPriceSuggestionPrompt.Build(facts, Market, PricePositioning.Higher, "it"));
    }
}
