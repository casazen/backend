using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ListingTextPromptTests
{
    private static ListingTextFacts Facts(
        IReadOnlyList<PropertyAmenity>? amenities = null,
        int photos = 2,
        IReadOnlyList<SimilarListingFacts>? similar = null) =>
        new(
            AccommodationType.Apartment,
            MaxGuests: 3,
            Bedrooms: 1,
            Beds: 2,
            Bathrooms: 2,
            AreaSqm: 75,
            amenities ?? [PropertyAmenity.WiFi, PropertyAmenity.AirConditioning],
            "Como",
            "CO",
            "Lombardia",
            AreaKind: null,
            FloorLevel: null,
            Pets: PetsPolicy.OnRequest,
            Smoking: SmokingPolicy.No,
            Parties: null,
            CheckInFrom: new TimeOnly(15, 0),
            CheckOutBy: new TimeOnly(10, 0),
            ChildrenWelcome: null,
            photos,
            similar ?? []);

    [Fact]
    public void BuildPrompt_FactsOfTheProperty_StatesThemInItalian()
    {
        var prompt = ListingTextPrompt.BuildPrompt(Facts());

        Assert.Contains("- Tipo: Appartamento", prompt);
        Assert.Contains("- Comune: Como (provincia CO), Lombardia", prompt);
        Assert.Contains("- Ospiti al massimo: 3", prompt);
        Assert.Contains("- Camere da letto: 1", prompt);
        Assert.Contains("- Letti: 2", prompt);
        Assert.Contains("- Bagni: 2", prompt);
        Assert.Contains("- Superficie: 75 m²", prompt);
        Assert.Contains("- Dotazioni indicate dall'host: Wi-Fi, Aria condizionata", prompt);
        Assert.Contains("- Animali: su richiesta", prompt);
        Assert.Contains("- Fumo: non ammesso", prompt);
        Assert.Contains("- Check-in dalle 15:00", prompt);
        Assert.Contains("Foto allegate: 2", prompt);
        Assert.Contains("Nessun altro annuncio è pubblicato su CasaZen in questo comune.", prompt);
    }

    [Fact]
    public void BuildPrompt_NoAmenitiesAndNoPhotos_TellsTheAiNotToInventThem()
    {
        var prompt = ListingTextPrompt.BuildPrompt(Facts(amenities: [], photos: 0));

        Assert.Contains("Dotazioni: non ancora indicate dall'host", prompt);
        Assert.Contains("Nessuna foto: scrivi solo dai dati.", prompt);
        Assert.DoesNotContain("Foto allegate", prompt);
    }

    [Fact]
    public void BuildPrompt_SimilarListings_ListsTheirPublicDataOnly()
    {
        var similar = new SimilarListingFacts(
            "Bilocale vista lago", AccommodationType.Apartment, 4, 2, 1, 60, [PropertyAmenity.FreeParking]);

        var prompt = ListingTextPrompt.BuildPrompt(Facts(similar: [similar, similar with { Title = null }]));

        Assert.Contains("Annunci pubblicati su CasaZen nello stesso comune (non copiarli):", prompt);
        Assert.Contains("1. «Bilocale vista lago» · Appartamento · 4 ospiti · 2 camere · 1 bagno · 60 m² · Parcheggio gratuito", prompt);
        Assert.Contains("2. Appartamento · 4 ospiti", prompt);
    }

    [Fact]
    public void SystemPrompt_AsksForTheTruthTheStructureAndJson()
    {
        Assert.Contains("Non inventare dotazioni", ListingTextPrompt.SystemPrompt);
        Assert.Contains("conosci il comune ma non l'indirizzo", ListingTextPrompt.SystemPrompt);
        Assert.Contains("tra 700 e 1.200 caratteri", ListingTextPrompt.SystemPrompt);
        Assert.Contains("{\"title\": \"...\", \"description\": \"...\"}", ListingTextPrompt.SystemPrompt);
    }

    [Fact]
    public void TryParseAnswer_JsonInACodeFence_ReadsTheTwoTexts()
    {
        const string answer = "Ecco il testo:\n```json\n{\"title\": \"«Bilocale luminoso a Como»!\", " +
            "\"description\": \"Primo paragrafo **luminoso**.\\n\\n\\n\\nSecondo   paragrafo.\"}\n```";

        Assert.True(ListingTextPrompt.TryParseAnswer(answer, out var title, out var description));
        Assert.Equal("Bilocale luminoso a Como", title);
        Assert.Equal("Primo paragrafo luminoso.\n\nSecondo paragrafo.", description);
    }

    [Fact]
    public void TryParseAnswer_TooLong_CutsAtAWordAndAtTheEndOfASentence()
    {
        var longTitle = string.Join(' ', Enumerable.Repeat("Appartamento", 8));
        var paragraph = string.Join(' ', Enumerable.Repeat("Una frase completa sul soggiorno.", 30));
        var answer = $"{{\"title\": \"{longTitle}\", \"description\": \"{paragraph}\\n\\n{paragraph}\\n\\n{paragraph}\"}}";

        Assert.True(ListingTextPrompt.TryParseAnswer(answer, out var title, out var description));
        Assert.True(title.Length <= ListingTextLimits.TitleMaxLength);
        Assert.EndsWith("Appartamento", title);
        Assert.True(description.Length <= ListingTextLimits.DescriptionMaxLength);
        Assert.EndsWith("soggiorno.", description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Non è JSON")]
    [InlineData("{\"title\": \"Solo il titolo\"}")]
    [InlineData("{\"title\": \"Titolo\", \"description\": 42}")]
    [InlineData("[\"title\", \"description\"]")]
    public void TryParseAnswer_NotAHeadlineAndADescription_IsFalse(string? answer)
    {
        Assert.False(ListingTextPrompt.TryParseAnswer(answer, out _, out _));
    }
}
