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
/// «Descrivi di cosa hai bisogno» (WIZ-AI-REQUEST): what goes to the provider (the host's words and nothing of the property), what comes back (only
/// what passes the checks), who is proposed (CasaZen whenever a supplier covers the comune: the rule is not the AI's), the cache, and the way a
/// missing, exhausted or failing provider reads for the host (never an error: a reason).
/// </summary>
public class ServiceRequestAssistServiceTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    private const string Words = "Il rubinetto della cucina perde acqua, serve qualcuno domani mattina";

    private const string GoodAnswer =
        """{"category":"plumbing","urgency":"High","summary":"Il rubinetto della cucina perde acqua: serve un intervento domani mattina.","reason":"Una perdita al rubinetto è un lavoro da idraulico."}""";

    private sealed record Rig(
        ServiceRequestAssistService Service,
        Mock<IAiProvider> Ai,
        Mock<ISupplierService> Suppliers,
        List<AiRequest> Requests,
        Property Property);

    private static Property MakeProperty(string? comune = "015072") => new()
    {
        Id = Guid.NewGuid(),
        OrgId = Org,
        OwnerId = "auth0|owner",
        Name = "Casa Rossi Brera",
        Address = "Via Giuseppe Garibaldi 14",
        City = "Seveso",
        ComuneIstatCode = comune,
    };

    private static SupplierProfile Supplier(string name) => new() { OrgId = Guid.NewGuid(), LegalName = name };

    private static Rig Create(
        string aiContent = GoodAnswer,
        AiGenerationResult? aiResult = null,
        bool aiOn = true,
        int covering = 2,
        string? comune = "015072")
    {
        var suppliers = new Mock<ISupplierService>();
        suppliers
            .Setup(s => s.GetActiveByComuneAsync(It.IsAny<ComuneTarget>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, covering).Select(i => Supplier($"Fornitore {i}")).ToList());

        var settings = new Mock<IAiRuntimeSettings>();
        settings
            .Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(aiOn ? new AiOptions { Provider = AiOptions.DeepSeekProvider, ApiKey = "test-key" } : new AiOptions());

        var requests = new List<AiRequest>();
        var ai = new Mock<IAiProvider>();
        ai
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, AiModelTier, string, CancellationToken>((request, _, _, _) => requests.Add(request))
            .ReturnsAsync(aiResult ?? new AiGenerationResult(aiContent, 150, 60, AiModelTier.Economy, false));

        var service = new ServiceRequestAssistService(
            ai.Object,
            settings.Object,
            new AiResponseCache(Options.Create(new AiCacheOptions())),
            suppliers.Object,
            NullLogger<ServiceRequestAssistService>.Instance);

        return new Rig(service, ai, suppliers, requests, MakeProperty(comune));
    }

    // ─── Availability ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsAvailable_ReflectsTheProviderInEffect()
    {
        Assert.True(await Create(aiOn: true).Service.IsAvailableAsync());
        Assert.False(await Create(aiOn: false).Service.IsAvailableAsync());
    }

    // ─── The suggestion ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Assist_AGoodAnswer_IsTheCategoryTheUrgencyAndTheTexts_WithCasazenWhenASupplierCoversTheComune()
    {
        var rig = Create(covering: 2);

        var result = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.True(result.AiUsed);
        Assert.Null(result.Reason);
        Assert.Equal(ServiceCategories.Plumbing, result.Category);
        Assert.Equal(ServiceRequestUrgency.High, result.Urgency);
        Assert.Equal("Il rubinetto della cucina perde acqua: serve un intervento domani mattina.", result.Summary);
        Assert.Equal("Una perdita al rubinetto è un lavoro da idraulico.", result.Explanation);
        Assert.Equal(ServiceRequestAssistChannels.CasaZen, result.Channel);
        Assert.Equal(2, result.CasazenSuppliers);
    }

    [Fact]
    public async Task Assist_TheSuppliersAreLookedForInTheComuneOfTheProperty_ForTheCategoryTheAiChose()
    {
        var rig = Create();

        await rig.Service.AssistAsync(rig.Property, Words, "it");

        rig.Suppliers.Verify(
            s => s.GetActiveByComuneAsync(
                It.Is<ComuneTarget>(target => target.IstatCode == "015072" && target.Name == "Seveso"),
                ServiceCategories.Plumbing,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Assist_WhenNoCasazenSupplierCoversTheComune_TheChannelIsExternal()
    {
        var rig = Create(covering: 0);

        var result = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.True(result.AiUsed);
        Assert.Equal(ServiceCategories.Plumbing, result.Category);
        Assert.Equal(ServiceRequestAssistChannels.External, result.Channel);
        Assert.Equal(0, result.CasazenSuppliers);
    }

    [Fact]
    public async Task Assist_ThePropertyWithoutAComuneOfTheOfficialList_HasNoCasazenCoverage_AndNobodyIsAsked()
    {
        var rig = Create(comune: null);

        var result = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.True(result.AiUsed);
        Assert.Equal(ServiceRequestAssistChannels.External, result.Channel);
        rig.Suppliers.Verify(
            s => s.GetActiveByComuneAsync(It.IsAny<ComuneTarget>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Assist_AnUnknownUrgency_IsNormal()
    {
        var rig = Create("""{"category":"cleaning","urgency":"asap","summary":"Pulizia di fine soggiorno.","reason":"Serve una pulizia."}""");

        var result = await rig.Service.AssistAsync(rig.Property, "Serve una pulizia di fine soggiorno per sabato", "it");

        Assert.Equal(ServiceRequestUrgency.Normal, result.Urgency);
    }

    [Fact]
    public async Task Assist_TheTextsOfTheAnswer_AreScrubbedOnceMore()
    {
        var rig = Create(
            """{"category":"plumbing","urgency":"High","summary":"Perdita in cucina, chiamare il 338 123 4567 o mario@example.com.","reason":"Scrivi a info@rossi.example."}""");

        var result = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.NotNull(result.Summary);
        Assert.StartsWith("Perdita in cucina", result.Summary);
        Assert.DoesNotContain("338", result.Summary);
        Assert.DoesNotContain("mario@", result.Summary);
        Assert.DoesNotContain("[tel]", result.Summary);
        Assert.DoesNotContain("[email]", result.Summary);
        Assert.DoesNotContain("info@", result.Explanation ?? string.Empty);
    }

    // ─── What goes to the provider ───────────────────────────────────────────────

    [Fact]
    public async Task Assist_TheRequestHoldsTheWordsOfTheHost_AndNothingOfTheProperty()
    {
        var rig = Create();

        await rig.Service.AssistAsync(rig.Property, Words, "it");

        var request = Assert.Single(rig.Requests);
        var everything = request.ToTextPrompt();
        Assert.Contains(Words, request.Prompt);
        Assert.DoesNotContain("Casa Rossi", everything);
        Assert.DoesNotContain("Garibaldi", everything);
        Assert.DoesNotContain("Seveso", everything);
        Assert.DoesNotContain("015072", everything);
        Assert.DoesNotContain(rig.Property.OwnerId, everything);
        Assert.Empty(request.Images);
    }

    [Fact]
    public async Task Assist_ContactDataAndAddressesTypedByTheHost_NeverReachTheProvider()
    {
        var rig = Create();

        await rig.Service.AssistAsync(
            rig.Property,
            "Perdita in cucina in via Giuseppe Garibaldi 14, chiamare Mario al 338 123 4567 oppure scrivere a mario@example.com",
            "it");

        var request = Assert.Single(rig.Requests);
        Assert.Contains("Perdita in cucina", request.Prompt);
        Assert.DoesNotContain("Garibaldi", request.Prompt);
        Assert.DoesNotContain("338", request.Prompt);
        Assert.DoesNotContain("mario@", request.Prompt);
        Assert.DoesNotContain("[tel]", request.Prompt);
    }

    [Fact]
    public async Task Assist_TheCallIsEconomyTier_AndTheSentenceForTheHostIsInTheLanguageOfTheRequest()
    {
        var rig = Create();

        await rig.Service.AssistAsync(rig.Property, Words, "en");

        rig.Ai.Verify(
            p => p.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Contains("in inglese, che spiega", Assert.Single(rig.Requests).SystemPrompt);
    }

    [Fact]
    public async Task Assist_NothingButContactData_IsNotUnderstood_AndTheProviderIsNotCalled()
    {
        var rig = Create();

        var result = await rig.Service.AssistAsync(rig.Property, "338 123 4567 mario@example.com", "it");

        Assert.False(result.AiUsed);
        Assert.Equal(ServiceRequestAssistReasons.NotUnderstood, result.Reason);
        Assert.Empty(rig.Requests);
    }

    // ─── When there is no suggestion ─────────────────────────────────────────────

    [Fact]
    public async Task Assist_WithoutAnActiveProvider_SaysSo_AndIsNotAnError()
    {
        var rig = Create(aiResult: new AiGenerationResult(string.Empty, 0, 0, AiModelTier.Economy, false, ProviderConfigured: false));

        var result = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.False(result.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiNotConfigured, result.Reason);
        Assert.Null(result.Category);
        Assert.Null(result.Channel);
    }

    [Fact]
    public async Task Assist_WhenTheBudgetIsUsedUp_SaysSo()
    {
        var rig = Create();
        rig.Ai
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        var result = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.False(result.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiBudgetExhausted, result.Reason);
    }

    [Fact]
    public async Task Assist_WhenTheProviderFailsOrTimesOut_SaysItIsUnavailable()
    {
        var rig = Create();
        rig.Ai
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        Assert.Equal(AiSuggestionFallbackReasons.AiUnavailable, (await rig.Service.AssistAsync(rig.Property, Words, "it")).Reason);

        rig.Ai
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        Assert.Equal(AiSuggestionFallbackReasons.AiUnavailable, (await rig.Service.AssistAsync(rig.Property, Words, "it")).Reason);
    }

    [Fact]
    public async Task Assist_WhenTheCallerLeaves_TheCancellationIsNotSwallowed()
    {
        var rig = Create();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        rig.Ai
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.AssistAsync(rig.Property, Words, "it", cancelled.Token));
    }

    [Theory]
    [InlineData("Non saprei dirti di cosa si tratta.")]
    [InlineData("""{"urgency":"High","summary":"Manca la categoria."}""")]
    [InlineData("""{"category":"piscina","urgency":"Normal"}""")]
    [InlineData("""{"category":"cleaning, plumbing","urgency":"Normal"}""")]
    [InlineData("")]
    public async Task Assist_AnAnswerThatIsNotAServiceOfThePlatform_IsInvalid_AndNeverCached(string content)
    {
        var rig = Create(content);

        var first = await rig.Service.AssistAsync(rig.Property, Words, "it");
        var second = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.False(first.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiInvalidAnswer, first.Reason);
        Assert.Equal(AiSuggestionFallbackReasons.AiInvalidAnswer, second.Reason);
        // A bad answer is not kept: the second call goes to the provider again.
        Assert.Equal(2, rig.Requests.Count);
    }

    [Fact]
    public async Task Assist_AJobTheModelCannotTell_IsNotUnderstood_NotAnError()
    {
        var rig = Create("""{"category":null,"urgency":"Normal","summary":"","reason":"Non è chiaro di che lavoro si tratti."}""");

        var result = await rig.Service.AssistAsync(rig.Property, "Mi serve una cosa per la casa", "it");

        Assert.False(result.AiUsed);
        Assert.Equal(ServiceRequestAssistReasons.NotUnderstood, result.Reason);
        Assert.Null(result.Channel);
        rig.Suppliers.Verify(
            s => s.GetActiveByComuneAsync(It.IsAny<ComuneTarget>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── The cache ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assist_TheSameWordsTwice_AreAskedOnce_ButTheChannelIsReadAgain()
    {
        var rig = Create(covering: 3);

        var first = await rig.Service.AssistAsync(rig.Property, Words, "it");
        var second = await rig.Service.AssistAsync(rig.Property, Words, "it");

        Assert.Single(rig.Requests);
        Assert.Equal(first.Category, second.Category);
        Assert.Equal(first.Summary, second.Summary);
        // The suppliers around the property change: the channel is never part of what is kept.
        rig.Suppliers.Verify(
            s => s.GetActiveByComuneAsync(It.IsAny<ComuneTarget>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Assist_OtherWordsOrAnotherLanguage_AreAnotherQuestion()
    {
        var rig = Create();

        await rig.Service.AssistAsync(rig.Property, Words, "it");
        await rig.Service.AssistAsync(rig.Property, Words + " e anche il lavandino", "it");
        await rig.Service.AssistAsync(rig.Property, Words, "en");

        Assert.Equal(3, rig.Requests.Count);
    }

    [Fact]
    public async Task Assist_TheAnswersOfAnOrg_AreNeverServedToAnother()
    {
        var rig = Create();
        var otherOrg = MakeProperty();
        otherOrg.OrgId = Guid.NewGuid();

        await rig.Service.AssistAsync(rig.Property, Words, "it");
        await rig.Service.AssistAsync(otherOrg, Words, "it");

        Assert.Equal(2, rig.Requests.Count);
    }
}
