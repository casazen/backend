using System.Text.Json.Nodes;
using Casazen.Web.Seo;
using Xunit;

namespace Casazen.Tests.Unit.Seo;

/// <summary>schema.org structured data of the public pages (BK-15): real data only, nothing invented, nothing empty.</summary>
public class SeoJsonLdTests
{
    private static SeoJsonLd.VacationRentalData Rental(
        decimal latitude = 45.8m,
        decimal longitude = 9.08m,
        int maxGuests = 4,
        int bathrooms = 2,
        decimal nightlyRate = 150m,
        bool petsAllowed = false,
        IReadOnlyList<string>? images = null,
        string? city = "Como",
        string? country = "IT") => new(
        "Villa Lago",
        "Una villa sul lago.",
        "https://public.example.test/book/rossi/property/villa-lago",
        images ?? ["https://cdn.example.test/1.jpg", "https://cdn.example.test/2.jpg"],
        city,
        "22100",
        country,
        latitude,
        longitude,
        maxGuests,
        2,
        bathrooms,
        ["Wi-Fi", "Piscina"],
        petsAllowed,
        "IT013075C2ABCDEFGH",
        nightlyRate,
        "EUR",
        "Rossi Ospitalità",
        "https://public.example.test/book/rossi");

    [Fact]
    public void VacationRental_PublishedProperty_HasTheFieldsOfSpecAc12ThatTheDataModelHolds()
    {
        var node = SeoJsonLd.VacationRental(Rental());

        Assert.Equal("https://schema.org", node["@context"]!.GetValue<string>());
        Assert.Equal("VacationRental", node["@type"]!.GetValue<string>());
        Assert.Equal("Villa Lago", node["name"]!.GetValue<string>());
        Assert.Equal("Una villa sul lago.", node["description"]!.GetValue<string>());
        Assert.Equal("https://public.example.test/book/rossi/property/villa-lago", node["url"]!.GetValue<string>());
        Assert.Equal(node["url"]!.GetValue<string>(), node["@id"]!.GetValue<string>());
        Assert.Equal("IT013075C2ABCDEFGH", node["identifier"]!.GetValue<string>());
        Assert.Equal(2, node["image"]!.AsArray().Count);
        Assert.Equal("https://cdn.example.test/1.jpg", node["image"]![0]!.GetValue<string>());

        var address = node["address"]!;
        Assert.Equal("PostalAddress", address["@type"]!.GetValue<string>());
        Assert.Equal("Como", address["addressLocality"]!.GetValue<string>());
        Assert.Equal("22100", address["postalCode"]!.GetValue<string>());
        Assert.Equal("IT", address["addressCountry"]!.GetValue<string>());

        var geo = node["geo"]!;
        Assert.Equal("GeoCoordinates", geo["@type"]!.GetValue<string>());
        Assert.Equal(45.8m, geo["latitude"]!.GetValue<decimal>());
        Assert.Equal(9.08m, geo["longitude"]!.GetValue<decimal>());

        var place = node["containsPlace"]!;
        Assert.Equal("Accommodation", place["@type"]!.GetValue<string>());
        Assert.Equal("EntirePlace", place["additionalType"]!.GetValue<string>());
        Assert.Equal(4, place["occupancy"]!["value"]!.GetValue<int>());
        Assert.Equal(2, place["numberOfBedrooms"]!.GetValue<int>());
        Assert.Equal(2, place["numberOfBathroomsTotal"]!.GetValue<int>());
        Assert.Equal(new[] { "Wi-Fi", "Piscina" }, place["amenityFeature"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
        Assert.All(place["amenityFeature"]!.AsArray(), amenity => Assert.True(amenity!["value"]!.GetValue<bool>()));

        Assert.Equal("Rossi Ospitalità", node["brand"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void VacationRental_NightlyRate_IsAnOfferOfTheBusinessWithAUnitPriceSpecification()
    {
        var node = SeoJsonLd.VacationRental(Rental(nightlyRate: 150m));

        var offer = Assert.Single(node["makesOffer"]!.AsArray())!;
        Assert.Equal("Offer", offer["@type"]!.GetValue<string>());
        var price = offer["priceSpecification"]!;
        Assert.Equal(150m, price["price"]!.GetValue<decimal>());
        Assert.Equal("EUR", price["priceCurrency"]!.GetValue<string>());
        Assert.Equal("DAY", price["unitCode"]!.GetValue<string>());
        Assert.Null(node["offers"]);
    }

    [Fact]
    public void VacationRental_DataTheModelDoesNotHold_IsNeverInvented()
    {
        var node = SeoJsonLd.VacationRental(Rental());

        // No review system, no check-in/out times, no availability: left out, not written as a guess.
        foreach (var key in new[] { "aggregateRating", "review", "checkinTime", "checkoutTime", "availability" })
            Assert.False(node.ContainsKey(key), key);
        // An absent PetFriendly amenity is not a "no pets" rule.
        Assert.False(node.ContainsKey("petsAllowed"));
    }

    [Fact]
    public void VacationRental_PetFriendlyAmenity_SaysPetsAreAllowed()
    {
        Assert.True(SeoJsonLd.VacationRental(Rental(petsAllowed: true))["petsAllowed"]!.GetValue<bool>());
    }

    [Fact]
    public void VacationRental_UnsetPosition_WritesNoCoordinates()
    {
        // 0,0 is the default of a position the host never set, not a place.
        var node = SeoJsonLd.VacationRental(Rental(latitude: 0m, longitude: 0m));

        Assert.False(node.ContainsKey("geo"));
    }

    [Fact]
    public void VacationRental_NoPriceNoGuestsNoImagesNoCountry_LeavesThoseKeysOut()
    {
        var node = SeoJsonLd.VacationRental(Rental(maxGuests: 0, bathrooms: 0, nightlyRate: 0m, images: [], country: null));

        Assert.False(node.ContainsKey("makesOffer"));
        Assert.False(node.ContainsKey("image"));
        Assert.False(node["address"]!.AsObject().ContainsKey("addressCountry"));
        Assert.False(node["containsPlace"]!.AsObject().ContainsKey("occupancy"));
        Assert.False(node["containsPlace"]!.AsObject().ContainsKey("numberOfBathroomsTotal"));
    }

    [Fact]
    public void BreadcrumbList_Items_ArePositionedListItemsWithTheirUrl()
    {
        var node = SeoJsonLd.BreadcrumbList([("Rossi", "https://public.example.test/book/rossi"), ("Villa", "https://public.example.test/book/rossi/property/villa")]);

        Assert.Equal("BreadcrumbList", node["@type"]!.GetValue<string>());
        var items = node["itemListElement"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0]!["position"]!.GetValue<int>());
        Assert.Equal("ListItem", items[0]!["@type"]!.GetValue<string>());
        Assert.Equal("https://public.example.test/book/rossi/property/villa", items[1]!["item"]!.GetValue<string>());
        // Nested items carry no @context of their own.
        Assert.False(items[0]!.AsObject().ContainsKey("@context"));
    }

    [Fact]
    public void Organization_WithoutLogoOrDescription_LeavesThemOut()
    {
        var node = SeoJsonLd.Organization("Rossi", "https://public.example.test/book/rossi", null, "  ");

        Assert.False(node.ContainsKey("logo"));
        Assert.False(node.ContainsKey("description"));
        Assert.Equal("Rossi", node["name"]!.GetValue<string>());
    }

    [Fact]
    public void Article_Guide_HasHeadlineModificationDateAndPublisherOfThePlatform()
    {
        var node = SeoJsonLd.Article(
            "Affitti brevi a Como", "Guida", "https://public.example.test/p/affitti-brevi/lombardia/como",
            new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc), "it", "CasaZen");

        Assert.Equal("Article", node["@type"]!.GetValue<string>());
        Assert.Equal("Affitti brevi a Como", node["headline"]!.GetValue<string>());
        Assert.Equal("2026-09-30", node["dateModified"]!.GetValue<string>());
        Assert.Equal("it", node["inLanguage"]!.GetValue<string>());
        Assert.Equal("CasaZen", node["publisher"]!["name"]!.GetValue<string>());
        Assert.False(node.ContainsKey("author"));
    }

    [Fact]
    public void Article_NoModificationDate_LeavesItOut()
    {
        Assert.False(SeoJsonLd.Article("T", null, null, null, "it", "CasaZen").ContainsKey("dateModified"));
    }
}
