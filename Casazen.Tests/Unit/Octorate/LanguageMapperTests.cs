using Casazen.Core.Entities;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

public class LanguageMapperTests
{
    private static Property House(string description = "Una villa sul lago.") => new()
    {
        Name = "Villa del Lago",
        ListingTitle = "Villa sul lago",
        Description = description,
    };

    [Fact]
    public void OctorateLanguages_AreTheFourteenOfTheChannelManager_AndConvertBothWays()
    {
        Assert.Equal(["IT", "EN", "FR", "ES", "DE", "RU", "PT", "NL", "JA", "EL", "TR", "ZH", "CA", "RO"], LanguageMapper.OctorateLanguages);
        Assert.Equal("FR", LanguageMapper.ToOctorate(" fr "));
        Assert.Equal("fr", LanguageMapper.FromOctorate("FR"));
        Assert.Null(LanguageMapper.FromOctorate("KO"));
        Assert.Throws<ArgumentException>(() => LanguageMapper.ToOctorate("ko"));
    }

    [Fact]
    public void ValidateForPublish_ListsTheMissingItalianAndEnglishHeadlineAndDescription()
    {
        Assert.Equal(["en.title", "en.description"], LanguageMapper.ValidateForPublish(House()));

        Assert.Equal(["it.description", "en.title", "en.description"], LanguageMapper.ValidateForPublish(House(description: "  ")));

        var property = House();
        property.SiteProfileJson = SitePropertyProfileJson.Serialize(new SitePropertyProfileDocument
        {
            Translations = new Dictionary<string, ListingTexts> { ["en"] = new ListingTexts(Title: "Lake villa", Description: "A villa on the lake.") },
        });
        Assert.Empty(LanguageMapper.ValidateForPublish(property));
    }

    [Fact]
    public void LanguageMap_KeysEachTextByTheCodeOfTheChannelManager()
    {
        var document = new SitePropertyProfileDocument
        {
            Translations = new Dictionary<string, ListingTexts>
            {
                ["en"] = new ListingTexts(Title: "Lake villa"),
                ["fr"] = new ListingTexts(Title: "Villa du lac"),
                ["de"] = new ListingTexts(Description: "Eine Villa."),
            },
        };

        var map = LanguageMapper.LanguageMap(House(), document, ListingTextField.Title);

        Assert.Equal(3, map.Count);
        Assert.Equal("Villa sul lago", map["IT"]);
        Assert.Equal("Lake villa", map["EN"]);
        Assert.Equal("Villa du lac", map["FR"]);
    }
}
