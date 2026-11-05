using Casazen.Core.Entities;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// NF-31: the languages of a listing are the 14 of the channel manager; a listing can be published when its headline and its description exist
/// in Italian and in English, and the mapper says which of the four are missing.
/// </summary>
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
        // Italian is in the columns of the property; English was never written.
        Assert.Equal(["en.title", "en.description"], LanguageMapper.ValidateForPublish(House()));

        // An Italian description left blank is missing too.
        Assert.Equal(["it.description", "en.title", "en.description"], LanguageMapper.ValidateForPublish(House(description: "  ")));

        // Written in English: nothing is missing, and the other languages are optional.
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
