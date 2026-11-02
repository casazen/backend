using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The AI help of the supplier showcase editor: what goes to the provider (true facts, no contact, no legal name), what comes back (only what passes
/// the checks of the static starter), the cache and the way a failing or missing provider reads for the editor.
/// </summary>
public class ShowcaseTextSuggestionServiceTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private const string GoodAnswer = """
        {
          "tagline": "Pulizie e giardinaggio per case vacanza",
          "intro": "Pulizie e cura del giardino a Cesano Maderno e a Seveso.",
          "bio": "Ci occupiamo di pulizie di fine soggiorno e di giardinaggio per case vacanza e appartamenti, a Cesano Maderno e a Seveso.",
          "prep": ["Lascia le chiavi alla portineria.", "Metti al sicuro gli oggetti di valore."]
        }
        """;

    private sealed record Rig(ShowcaseTextSuggestionService Service, Mock<IAiProvider> Ai, List<string> Prompts);

    private static SupplierProfile Profile() => new()
    {
        OrgId = Org,
        LegalName = "Rossi Pulizie Srl",
        Phone = "+39 338 123 4567",
        Email = "info@rossi.example",
        CategoriesJson = """["cleaning","gardening"]""",
        ComuneIstatCodesJson = """["015072"]""",
        ComuniJson = """["Seveso","H501","20822"]""",
    };

    private static IReadOnlyList<SupplierServiceListing> Listings() =>
    [
        new SupplierServiceListing { OrgId = Org, Name = "Pulizia di fine soggiorno", Category = "cleaning" },
        new SupplierServiceListing { OrgId = Org, Name = "Taglio del prato", Category = "gardening" },
    ];

    private static ShowcaseContentView View(ShowcaseContentDocument? draft = null) =>
        new(0, ShowcaseContentState.Draft, draft ?? new ShowcaseContentDocument(), null, [], null, null, null, null, new ShowcaseReadinessResult([]), new ShowcaseBookings(false, []));

    private static Rig Create(
        bool aiOn = true,
        SupplierProfile? profile = null,
        IReadOnlyList<SupplierServiceListing>? listings = null,
        ShowcaseContentDocument? draft = null,
        string aiContent = GoodAnswer,
        AiGenerationResult? aiResult = null)
    {
        var suppliers = new Mock<ISupplierService>();
        suppliers.Setup(s => s.GetProfileAsync(Org, It.IsAny<CancellationToken>())).ReturnsAsync(profile ?? Profile());

        var catalog = new Mock<ISupplierServiceCatalogService>();
        catalog.Setup(c => c.ListAsync(Org, It.IsAny<CancellationToken>())).ReturnsAsync(listings ?? Listings());

        var comuni = new Mock<IComuneDirectory>();
        comuni
            .Setup(c => c.GetByIstatCodesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, Comune> { ["015072"] = new() { IstatCode = "015072", Name = "Cesano Maderno", DisplayName = "Cesano Maderno" } });

        var content = new Mock<ISupplierShowcaseContentService>();
        content.Setup(c => c.GetAsync(Org, It.IsAny<CancellationToken>())).ReturnsAsync(View(draft));

        var settings = new Mock<IAiRuntimeSettings>();
        settings
            .Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(aiOn ? new AiOptions { Provider = AiOptions.DeepSeekProvider, ApiKey = "test-key" } : new AiOptions());

        var prompts = new List<string>();
        var ai = new Mock<IAiProvider>();
        ai
            .Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, AiModelTier, string, CancellationToken>((prompt, _, _, _) => prompts.Add(prompt))
            .ReturnsAsync(aiResult ?? new AiGenerationResult(aiContent, 120, 80, AiModelTier.Standard, false));

        var service = new ShowcaseTextSuggestionService(
            suppliers.Object,
            catalog.Object,
            comuni.Object,
            content.Object,
            ai.Object,
            settings.Object,
            new AiResponseCache(Options.Create(new AiCacheOptions())),
            NullLogger<ShowcaseTextSuggestionService>.Instance);

        return new Rig(service, ai, prompts);
    }

    // ─── Availability ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsAvailable_ReflectsTheProviderInEffect()
    {
        Assert.True(await Create(aiOn: true).Service.IsAvailableAsync());
        Assert.False(await Create(aiOn: false).Service.IsAvailableAsync());
    }

    [Fact]
    public async Task Suggest_NoProvider_IsAnAiUnavailableAndNothingIsSent()
    {
        var rig = Create(aiOn: false);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest()));

        Assert.Equal(ShowcaseContentErrors.AiUnavailable, error.Code);
        Assert.Empty(rig.Prompts);
    }

    // ─── What goes to the provider ───────────────────────────────────────────────

    [Fact]
    public async Task Suggest_ThePromptHoldsTheTrueFactsAndNoContactNorLegalName()
    {
        var rig = Create();
        var answers = new ShowcaseTextAnswers("Siamo una piccola squadra. Chiamaci al 338 123 4567 o scrivi a info@rossi.example.", "dal 2015", null, null);

        await rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest(Answers: answers));

        var prompt = Assert.Single(rig.Prompts);
        Assert.Contains("pulizie, giardinaggio", prompt);
        Assert.Contains("Cesano Maderno, Seveso", prompt);
        Assert.Contains("Pulizia di fine soggiorno, Taglio del prato", prompt);
        Assert.Contains("Siamo una piccola squadra.", prompt);
        Assert.Contains("dal 2015", prompt);

        // Not the legal name, not the contact data of the profile, not what the supplier typed that looks like a phone or an e-mail, not an old code.
        Assert.DoesNotContain("Rossi", prompt);
        Assert.DoesNotContain("338", prompt);
        Assert.DoesNotContain("info@", prompt);
        Assert.DoesNotContain("[tel]", prompt);
        Assert.DoesNotContain("H501", prompt);
        Assert.DoesNotContain("20822", prompt);
    }

    [Fact]
    public async Task Suggest_TheTextsAlreadyInTheDraftAreOnlyAToneToKeep()
    {
        var draft = new ShowcaseContentDocument { Tagline = "Pulizie per case vacanza", Prep = ["Lascia le chiavi alla portineria."] };
        var rig = Create(draft: draft);

        await rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest(Fields: [ShowcaseTextFields.Bio]));

        var prompt = Assert.Single(rig.Prompts);
        Assert.Contains("TESTI GIÀ SCRITTI DAL FORNITORE (non riscriverli)", prompt);
        Assert.Contains("Pulizie per case vacanza", prompt);
        Assert.Contains("SOLO queste chiavi: bio", prompt);
    }

    [Fact]
    public async Task Suggest_UsesTheStandardTier()
    {
        var rig = Create();

        await rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest());

        rig.Ai.Verify(p => p.GenerateAsync(It.IsAny<string>(), AiModelTier.Standard, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Suggest_NothingTrueToWriteFrom_IsRefusedBeforeTheProviderIsCalled()
    {
        var empty = new SupplierProfile { OrgId = Org };
        var rig = Create(profile: empty, listings: []);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest()));

        Assert.Equal(ShowcaseContentErrors.SuggestionNotEnoughData, error.Code);
        Assert.Empty(rig.Prompts);
    }

    [Fact]
    public async Task Suggest_NothingInTheProfileButTheSupplierTypedAnAnswer_StillWrites()
    {
        var empty = new SupplierProfile { OrgId = Org };
        var rig = Create(profile: empty, listings: [], aiContent: """{"tagline": "Pulizie per case vacanza"}""");

        var suggestion = await rig.Service.SuggestAsync(
            Org,
            new ShowcaseTextSuggestionRequest([ShowcaseTextFields.Tagline], new ShowcaseTextAnswers(About: "Facciamo pulizie per case vacanza.")));

        Assert.Equal("Pulizie per case vacanza", suggestion.Tagline);
        Assert.Equal(new[] { ShowcaseTextAnswerKeys.About }, suggestion.Basis.Answers);
    }

    // ─── What comes back ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Suggest_AGoodAnswer_ReturnsTheDraftsAndWhatTheyWereBasedOn()
    {
        var rig = Create();

        var suggestion = await rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest(Answers: new ShowcaseTextAnswers(About: "Siamo una piccola squadra.")));

        Assert.Equal("Pulizie e giardinaggio per case vacanza", suggestion.Tagline);
        Assert.StartsWith("Pulizie e cura del giardino", suggestion.Intro);
        Assert.StartsWith("Ci occupiamo di pulizie", suggestion.Bio);
        Assert.Equal(2, suggestion.Prep.Count);
        Assert.Empty(suggestion.Rejected);
        Assert.True(suggestion.AiUsed);
        Assert.Equal(new[] { "cleaning", "gardening" }, suggestion.Basis.Categories);
        Assert.Equal(new[] { "Cesano Maderno", "Seveso" }, suggestion.Basis.Zones);
        Assert.Equal(new[] { "Pulizia di fine soggiorno", "Taglio del prato" }, suggestion.Basis.Services);
        Assert.Equal(new[] { ShowcaseTextAnswerKeys.About }, suggestion.Basis.Answers);
    }

    [Fact]
    public async Task Suggest_AMemberWithAClaimTheSupplierDidNotMake_IsDroppedAndTheOthersStay()
    {
        var rig = Create(aiContent: """
            {"tagline": "Pulizie per case vacanza", "intro": "Garantiamo pulizie in 24 ore a Seveso.", "bio": "Ci occupiamo di pulizie a Seveso."}
            """);

        var suggestion = await rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest(Fields: [ShowcaseTextFields.Tagline, ShowcaseTextFields.Intro, ShowcaseTextFields.Bio]));

        Assert.Equal("Pulizie per case vacanza", suggestion.Tagline);
        Assert.Null(suggestion.Intro);
        Assert.Equal("Ci occupiamo di pulizie a Seveso.", suggestion.Bio);
        Assert.Equal(new[] { ShowcaseTextFields.Intro }, suggestion.Rejected);
    }

    [Fact]
    public async Task Suggest_EveryMemberDropped_IsAnUnusableSuggestion()
    {
        var rig = Create(aiContent: """{"tagline": "Pulizie professionali garantite", "intro": "Siamo i migliori in 24 ore"}""");

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest(Fields: [ShowcaseTextFields.Tagline, ShowcaseTextFields.Intro])));

        Assert.Equal(ShowcaseContentErrors.SuggestionUnusable, error.Code);
    }

    [Fact]
    public async Task Suggest_AClaimTheSupplierTypedItself_MayBeRepeated()
    {
        var rig = Create(aiContent: """{"bio": "Lavoriamo dal 2015 a Cesano Maderno e a Seveso."}""");

        var suggestion = await rig.Service.SuggestAsync(
            Org,
            new ShowcaseTextSuggestionRequest([ShowcaseTextFields.Bio], new ShowcaseTextAnswers(Years: "dal 2015")));

        Assert.Equal("Lavoriamo dal 2015 a Cesano Maderno e a Seveso.", suggestion.Bio);
    }

    // ─── The provider ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Suggest_AProviderThatFails_IsAnAiUnavailableForTheEditor()
    {
        var rig = Create();
        rig.Ai
            .Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest()));

        Assert.Equal(ShowcaseContentErrors.AiUnavailable, error.Code);
    }

    [Fact]
    public async Task Suggest_ThePlaceholderOfTheStub_IsAnAiUnavailableToo()
    {
        var rig = Create(aiResult: new AiGenerationResult(string.Empty, 0, 0, AiModelTier.Standard, false, ProviderConfigured: false));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest()));

        Assert.Equal(ShowcaseContentErrors.AiUnavailable, error.Code);
    }

    [Fact]
    public async Task Suggest_TheBudgetOfThePlatformIsSpent_KeepsItsOwnAnswer()
    {
        var rig = Create();
        rig.Ai
            .Setup(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        await Assert.ThrowsAsync<AiBudgetExceededException>(() => rig.Service.SuggestAsync(Org, new ShowcaseTextSuggestionRequest()));
    }

    // ─── The cache ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Suggest_TheSameInputsTwice_CallTheProviderOnce_AndRegenerateCallsItAgain()
    {
        var rig = Create();
        var request = new ShowcaseTextSuggestionRequest();

        var first = await rig.Service.SuggestAsync(Org, request);
        var second = await rig.Service.SuggestAsync(Org, request);

        Assert.Equal(first.Tagline, second.Tagline);
        rig.Ai.Verify(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        await rig.Service.SuggestAsync(Org, request with { Regenerate = true });

        rig.Ai.Verify(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Suggest_AnAnswerThatGaveNothingUsable_IsNotKept()
    {
        var rig = Create(aiContent: """{"tagline": "Pulizie garantite"}""");
        var request = new ShowcaseTextSuggestionRequest([ShowcaseTextFields.Tagline]);

        await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.SuggestAsync(Org, request));
        await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.SuggestAsync(Org, request));

        rig.Ai.Verify(p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
