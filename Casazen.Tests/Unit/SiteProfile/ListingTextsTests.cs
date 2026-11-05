using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.SiteProfile;
using Xunit;

namespace Casazen.Tests.Unit.SiteProfile;

/// <summary>
/// NF-31: the texts of a listing in the languages of the channel manager live in the site profile document, each with its origin; one reader
/// (<see cref="ListingTextResolver"/>) gives them back by language and field; a person's text is never taken for a machine's nor overwritten
/// by one; and the public page keeps reading Italian and English only.
/// </summary>
public class ListingTextsTests
{
    private const int Year = 2026;

    private static SitePropertyProfileInput Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private static Dictionary<string, ListingTextsInput> Translations(params (string Language, ListingTextsInput Texts)[] texts) =>
        texts.ToDictionary(t => t.Language, t => t.Texts, StringComparer.Ordinal);

    private static SitePropertyProfileDocument Save(SitePropertyProfileInput input, SitePropertyProfileDocument? previous = null) =>
        SitePropertyProfileRules.Normalize(input, null, Year, previous).Document;

    private static Property House() => new()
    {
        Name = "Villa del Lago",
        ListingTitle = "Villa sul lago",
        Description = "Una villa con giardino.",
        HouseRules = "Non fumare.",
    };

    private static SitePropertyProfileDocument Profile() => new()
    {
        Zone = new LocalizedText("Lungolago", "Lakefront") { Others = new Dictionary<string, string> { ["fr"] = "Bord du lac" } },
        Near = new LocalizedText("Vicino al centro", "Near the centre"),
        FloorNote = new LocalizedText("Piano terra", "Ground floor"),
        OutdoorNote = new LocalizedText("Giardino", "Garden"),
        CheckInMethod = new LocalizedText("Cassetta a codice", "Key box"),
        RulesNote = new LocalizedText("Silenzio dopo le 22.", "Quiet after 10 pm."),
        Translations = new Dictionary<string, ListingTexts>
        {
            ["it"] = new ListingTexts(Transport: "Bus 12 dalla stazione"),
            ["en"] = new ListingTexts(Title: "Lake villa", Description: "A villa with a garden.", Rules: "No smoking."),
        },
    };

    // ─── What is stored ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_Translations_AreCleanedKeyedByLanguageAndItalianKeepsOnlyTheTransport()
    {
        var document = Save(Empty with
        {
            Translations = Translations(
                (" EN ", new ListingTextsInput(Title: "  Lake villa ", Description: "A house\r\non the lake.", Spaces: "Ground floor", Transport: "   ")),
                ("it", new ListingTextsInput(Title: "ignored", Description: "ignored", Transport: " Bus 12 "))),
        });

        Assert.Equal(["en", "it"], document.Translations.Keys);
        var english = document.Translations["en"];
        Assert.Equal("Lake villa", english.Title);
        Assert.Equal("A house\non the lake.", english.Description);
        Assert.Equal("Ground floor", english.Spaces);
        Assert.Null(english.Transport);
        var italian = document.Translations["it"];
        Assert.Equal("Bus 12", italian.Transport);
        Assert.Null(italian.Title);
        Assert.Null(italian.Description);
        Assert.Equal(["en.description", "en.spaces", "en.title", "it.transport"], document.Origins.Keys);
        Assert.All(document.Origins.Values, origin => Assert.Equal(TextOrigin.UserWritten, origin));
    }

    [Fact]
    public void Normalize_ATranslationTooLongOrInAnUnknownLanguage_IsRefusedWithTheFieldThatBrokeTheRule()
    {
        var title = Assert.Throws<DomainRuleException>(() => Save(Empty with
        {
            Translations = Translations(("fr", new ListingTextsInput(Title: new string('a', 61)))),
        }));
        Assert.Equal(SitePropertyProfileErrorCodes.TextTooLong, title.Code);
        Assert.Equal("translations.fr.title", title.Extensions["field"]);

        var atTheLimit = Save(Empty with
        {
            Translations = Translations(("de", new ListingTextsInput(Rules: new string('a', 1000)))),
        });
        Assert.Equal(1000, atTheLimit.Translations["de"].Rules!.Length);

        var rules = Assert.Throws<DomainRuleException>(() => Save(Empty with
        {
            Translations = Translations(("de", new ListingTextsInput(Rules: new string('a', 1001)))),
        }));
        Assert.Equal("translations.de.rules", rules.Extensions["field"]);

        var unknown = Assert.Throws<DomainRuleException>(() => Save(Empty with
        {
            Translations = Translations(("xx", new ListingTextsInput(Title: "x"))),
        }));
        Assert.Equal(SitePropertyProfileErrorCodes.LanguageUnknown, unknown.Code);
        Assert.Equal("translations", unknown.Extensions["field"]);
    }

    [Fact]
    public void Normalize_TranslationsLeftOut_KeepsTheStoredOnes_AndAnEmptyMapClearsThem()
    {
        var stored = Save(Empty with { Translations = Translations(("fr", new ListingTextsInput(Title: "Villa du lac"))) });

        var left = Save(Empty, stored);
        Assert.Equal("Villa du lac", left.Translations["fr"].Title);
        Assert.Equal(TextOrigin.UserWritten, left.Origins["fr.title"]);

        var cleared = Save(Empty with { Translations = new Dictionary<string, ListingTextsInput>() }, stored);
        Assert.Empty(cleared.Translations);
        Assert.Empty(cleared.Origins);
    }

    [Fact]
    public void Normalize_APersonSaves_GivesEachTextTheOriginFollowingTheOneStored()
    {
        var stored = new SitePropertyProfileDocument
        {
            Translations = new Dictionary<string, ListingTexts>
            {
                ["fr"] = new ListingTexts(Title: "Villa du lac", Description: "Une maison sur le lac.", Spaces: "Rez-de-chaussee", Access: "Boite a cles"),
            },
            Origins = new Dictionary<string, TextOrigin>
            {
                ["fr.title"] = TextOrigin.AutoTranslated,
                ["fr.description"] = TextOrigin.AutoTranslated,
                ["fr.spaces"] = TextOrigin.ModifiedAfterTranslation,
                ["fr.access"] = TextOrigin.UserWritten,
            },
        };

        var saved = Save(
            Empty with
            {
                Translations = Translations(("fr", new ListingTextsInput(
                    Title: "Villa du lac",
                    Description: "Une maison sur le lac de Come.",
                    Spaces: "Rez-de-chaussee et jardin",
                    Access: "Boite a cles a code",
                    Neighborhood: "Au bord du lac"))),
            },
            stored);

        Assert.Equal(TextOrigin.AutoTranslated, saved.Origins["fr.title"]);
        Assert.Equal(TextOrigin.ModifiedAfterTranslation, saved.Origins["fr.description"]);
        Assert.Equal(TextOrigin.ModifiedAfterTranslation, saved.Origins["fr.spaces"]);
        Assert.Equal(TextOrigin.UserWritten, saved.Origins["fr.access"]);
        Assert.Equal(TextOrigin.UserWritten, saved.Origins["fr.neighborhood"]);
    }

    [Fact]
    public void Normalize_OthersOfAProfileText_AreKeptWhenNotSent_ReplacedWhenSent_AndCheckedAgainstTheLanguagesAndTheLength()
    {
        var stored = Save(Empty with
        {
            Zone = new LocalizedTextInput("Lungolago", "Lakefront", new Dictionary<string, string?> { ["FR"] = "Bord du lac", ["de"] = "  " }),
        });
        var storedZone = stored.Zone!;
        Assert.Equal("Bord du lac", storedZone.Others!["fr"]);
        Assert.False(storedZone.Others.ContainsKey("de"));

        var kept = Save(Empty with { Zone = new LocalizedTextInput("Lungolago", "Lakefront") }, stored);
        Assert.Equal("Bord du lac", kept.Zone!.Others!["fr"]);

        var removed = Save(Empty with { Zone = new LocalizedTextInput("Lungolago", "Lakefront", new Dictionary<string, string?>()) }, stored);
        Assert.Null(removed.Zone!.Others);

        Assert.Null(Save(Empty, stored).Zone);

        var english = Assert.Throws<DomainRuleException>(() => Save(Empty with
        {
            Zone = new LocalizedTextInput("Lungolago", null, new Dictionary<string, string?> { ["en"] = "Lakefront" }),
        }));
        Assert.Equal(SitePropertyProfileErrorCodes.LanguageUnknown, english.Code);
        Assert.Equal("zone", english.Extensions["field"]);

        var longer = Assert.Throws<DomainRuleException>(() => Save(Empty with
        {
            Zone = new LocalizedTextInput(
                "Lungolago", null, new Dictionary<string, string?> { ["fr"] = new string('a', SitePropertyProfileLimits.ZoneMaxLength + 1) }),
        }));
        Assert.Equal(SitePropertyProfileErrorCodes.TextTooLong, longer.Code);
    }

    // ─── The origin of a text ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, TextOrigin.AutoTranslated, "Nuovo", TextOrigin.UserWritten)]
    [InlineData("Uguale", TextOrigin.AutoTranslated, "Uguale", TextOrigin.AutoTranslated)]
    [InlineData("Uguale", TextOrigin.UserWritten, "Uguale", TextOrigin.UserWritten)]
    [InlineData("Prima", TextOrigin.AutoTranslated, "Dopo", TextOrigin.ModifiedAfterTranslation)]
    [InlineData("Prima", TextOrigin.ModifiedAfterTranslation, "Dopo", TextOrigin.ModifiedAfterTranslation)]
    [InlineData("Prima", TextOrigin.UserWritten, "Dopo", TextOrigin.UserWritten)]
    public void WhenPersonSaves_GivesTheOriginOfTheTextSaved(string? previousText, TextOrigin previousOrigin, string text, TextOrigin expected) =>
        Assert.Equal(expected, ListingTextOrigins.WhenPersonSaves(text, previousText, previousOrigin));

    [Theory]
    [InlineData(null, TextOrigin.UserWritten, true)]
    [InlineData("  ", TextOrigin.UserWritten, true)]
    [InlineData("Testo", TextOrigin.AutoTranslated, true)]
    [InlineData("Testo", TextOrigin.UserWritten, false)]
    [InlineData("Testo", TextOrigin.ModifiedAfterTranslation, false)]
    public void MachineMayWrite_OnlyWhereNothingIsOrOverAMachineTranslation(string? previousText, TextOrigin previousOrigin, bool expected) =>
        Assert.Equal(expected, ListingTextOrigins.MachineMayWrite(previousText, previousOrigin));

    [Fact]
    public void MergeAutoTranslations_WritesWhereNothingIsOrOverAMachineTranslation_AndNeverOverAPersonsText()
    {
        var document = new SitePropertyProfileDocument
        {
            Translations = new Dictionary<string, ListingTexts>
            {
                ["fr"] = new ListingTexts(Title: "Titre ecrit", Description: "Description traduite", Spaces: "Espaces modifies"),
            },
            Origins = new Dictionary<string, TextOrigin>
            {
                ["fr.title"] = TextOrigin.UserWritten,
                ["fr.description"] = TextOrigin.AutoTranslated,
                ["fr.spaces"] = TextOrigin.ModifiedAfterTranslation,
            },
        };

        var merged = SitePropertyProfileRules.MergeAutoTranslations(
            document,
            Translations(
                ("fr", new ListingTextsInput(Title: "Titre machine", Description: "Nouvelle description", Spaces: "Espaces machine", Access: "Acces machine")),
                ("de", new ListingTextsInput(Title: "Titel")),
                ("it", new ListingTextsInput(Title: "ignorato"))));

        var french = merged.Translations["fr"];
        Assert.Equal("Titre ecrit", french.Title);
        Assert.Equal(TextOrigin.UserWritten, merged.Origins["fr.title"]);
        Assert.Equal("Nouvelle description", french.Description);
        Assert.Equal(TextOrigin.AutoTranslated, merged.Origins["fr.description"]);
        Assert.Equal("Espaces modifies", french.Spaces);
        Assert.Equal(TextOrigin.ModifiedAfterTranslation, merged.Origins["fr.spaces"]);
        Assert.Equal("Acces machine", french.Access);
        Assert.Equal(TextOrigin.AutoTranslated, merged.Origins["fr.access"]);
        Assert.Equal("Titel", merged.Translations["de"].Title);
        Assert.Equal(TextOrigin.AutoTranslated, merged.Origins["de.title"]);
        Assert.False(merged.Translations.ContainsKey("it"));
    }

    // ─── The one reader ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolver_Italian_ComesFromTheColumnsAndTheItalianPartsOfTheProfileTexts()
    {
        var house = House();
        var profile = Profile();

        Assert.Equal("Villa sul lago", ListingTextResolver.Get(house, profile, "it", ListingTextField.Title));
        Assert.Equal("Una villa con giardino.", ListingTextResolver.Get(house, profile, "it", ListingTextField.Description));
        Assert.Equal("Piano terra\nGiardino", ListingTextResolver.Get(house, profile, "it", ListingTextField.Spaces));
        Assert.Equal("Cassetta a codice", ListingTextResolver.Get(house, profile, "it", ListingTextField.Access));
        Assert.Equal("Lungolago\nVicino al centro", ListingTextResolver.Get(house, profile, "it", ListingTextField.Neighborhood));
        Assert.Equal("Bus 12 dalla stazione", ListingTextResolver.Get(house, profile, "IT", ListingTextField.Transport));
        Assert.Equal("Non fumare.\nSilenzio dopo le 22.", ListingTextResolver.Get(house, profile, "it", ListingTextField.Rules));

        // No headline written: the listing uses the name of the property.
        house.ListingTitle = null;
        Assert.Equal("Villa del Lago", ListingTextResolver.Get(house, profile, "it", ListingTextField.Title));
    }

    [Fact]
    public void Resolver_AnotherLanguage_ComesFromTheDocument_AndFallsBackOnTheProfileTextsOnlyWhereTheyAreTheWholeField()
    {
        var house = House();
        var profile = Profile();

        Assert.Equal("Lake villa", ListingTextResolver.Get(house, profile, "en", ListingTextField.Title));
        Assert.Equal("A villa with a garden.", ListingTextResolver.Get(house, profile, "en", ListingTextField.Description));
        Assert.Equal("No smoking.", ListingTextResolver.Get(house, profile, "en", ListingTextField.Rules));

        // Spaces, access and neighborhood were never written for English: the English parts of the profile texts are the field.
        Assert.Equal("Ground floor\nGarden", ListingTextResolver.Get(house, profile, "en", ListingTextField.Spaces));
        Assert.Equal("Key box", ListingTextResolver.Get(house, profile, "en", ListingTextField.Access));
        Assert.Equal("Lakefront\nNear the centre", ListingTextResolver.Get(house, profile, "en", ListingTextField.Neighborhood));

        // The transport has no fall back, and neither has a language nobody wrote.
        Assert.Null(ListingTextResolver.Get(house, profile, "en", ListingTextField.Transport));
        Assert.Null(ListingTextResolver.Get(house, profile, "fr", ListingTextField.Title));
        Assert.Null(ListingTextResolver.Get(house, profile, "fr", ListingTextField.Spaces));
        Assert.Equal("Bord du lac", ListingTextResolver.Get(house, profile, "fr", ListingTextField.Neighborhood));
        Assert.Null(ListingTextResolver.Get(house, profile, "xx", ListingTextField.Title));

        // What was written for the field wins over the fall back.
        var written = profile with
        {
            Translations = new Dictionary<string, ListingTexts> { ["en"] = new ListingTexts(Neighborhood: "By the lake") },
        };
        Assert.Equal("By the lake", ListingTextResolver.Get(house, written, "en", ListingTextField.Neighborhood));
    }

    // ─── Storage and the public page ───────────────────────────────────────────────────────────────

    [Fact]
    public void Document_RoundTripsThroughTheColumn_AndKeepsOnlyOriginsOfTextsThatExist()
    {
        var document = Profile() with
        {
            Origins = new Dictionary<string, TextOrigin>
            {
                ["en.title"] = TextOrigin.AutoTranslated,
                ["en.rules"] = TextOrigin.ModifiedAfterTranslation,
                ["fr.title"] = TextOrigin.UserWritten,
            },
        };

        var parsed = SitePropertyProfileJson.Parse(SitePropertyProfileJson.Serialize(document));

        Assert.Equal("Lake villa", parsed.Translations["en"].Title);
        Assert.Equal("Bus 12 dalla stazione", parsed.Translations["it"].Transport);
        Assert.Equal("Bord du lac", parsed.Zone!.Others!["fr"]);
        Assert.Equal(["en.rules", "en.title"], parsed.Origins.Keys);
        Assert.Equal(TextOrigin.AutoTranslated, parsed.Origins["en.title"]);
        Assert.Equal(TextOrigin.ModifiedAfterTranslation, parsed.Origins["en.rules"]);
    }

    [Fact]
    public void ThePublicPage_ReadsItalianAndEnglishOnly_ThoughTheDocumentKeepsTheOtherLanguages()
    {
        var json = SitePropertyProfileJson.Serialize(Profile());

        var page = PublicPropertyProfileBuilder.Build(new PublicProfileSource(
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, json, []));

        Assert.Equal("Lungolago", page.Zone!.It);
        Assert.Equal("Lakefront", page.Zone.En);
        Assert.Null(page.Zone.Others);
        Assert.Equal("Key box", page.Rules!.CheckIn!.Method!.En);
        Assert.Null(page.Rules.CheckIn.Method.Others);

        var forThePage = SitePropertyProfileJson.ParseForPublicPage(json);
        Assert.Empty(forThePage.Translations);
        Assert.Empty(forThePage.Origins);
        Assert.Null(forThePage.Zone!.Others);
    }
}
