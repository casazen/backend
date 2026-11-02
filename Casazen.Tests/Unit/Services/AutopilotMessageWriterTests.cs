using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The chat messages of «Gestisci con AI»: what goes to the provider (a language and the number of the reminder, nothing of a guest), what comes
/// back (only texts that pass the checks, one by one), the cache, the permit of the AI limits, and the way a missing, exhausted or failing
/// provider reads for the host (never an error: the plain texts and a reason).
/// </summary>
public class AutopilotMessageWriterTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-0000000000d4");

    private const string ItalianText =
        "Ciao {nome}, per registrare il tuo soggiorno a {struttura} servono i dati di tutti gli ospiti, con un documento d'identità. Bastano pochi minuti: {link}";

    private const string GermanText =
        "Hallo {nome}, eine freundliche Erinnerung: Für die Anmeldung Ihres Aufenthalts in {struttura} fehlen uns noch die Daten aller Gäste. {link}";

    private static readonly string GoodAnswer = $$"""{"it-1":"{{ItalianText}}","de-2":"{{GermanText}}"}""";

    private static readonly AutopilotChatVariant[] Variants = [new("it", 1), new("de", 2)];

    private sealed record Rig(AutopilotMessageWriter Writer, Mock<IAiProvider> Ai, List<AiRequest> Requests);

    private static Rig Create(string content = "", bool aiOn = true, Exception? failure = null, bool providerConfigured = true)
    {
        var settings = new Mock<IAiRuntimeSettings>();
        settings
            .Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(aiOn ? new AiOptions { Provider = AiOptions.DeepSeekProvider, ApiKey = "test-key" } : new AiOptions());

        var requests = new List<AiRequest>();
        var ai = new Mock<IAiProvider>();
        var setup = ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
        setup.Callback<AiRequest, AiModelTier, string, CancellationToken>((request, _, _, _) => requests.Add(request));
        if (failure is not null)
            setup.ThrowsAsync(failure);
        else
            setup.ReturnsAsync(new AiGenerationResult(content.Length == 0 ? GoodAnswer : content, 120, 90, AiModelTier.Economy, false, providerConfigured));

        var writer = new AutopilotMessageWriter(
            ai.Object,
            settings.Object,
            new AiResponseCache(Options.Create(new AiCacheOptions())),
            NullLogger<AutopilotMessageWriter>.Instance);
        return new Rig(writer, ai, requests);
    }

    [Fact]
    public async Task Write_NothingAsked_NothingIsCalled()
    {
        var rig = Create();

        var wording = await rig.Writer.WriteAsync(Org, [], null);

        Assert.Empty(wording.Texts);
        Assert.False(wording.AiUsed);
        Assert.Null(wording.AiReason);
        rig.Ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Write_NoProviderInEffect_ThePlainTextsAndTheReason_WithoutACall()
    {
        var rig = Create(aiOn: false);

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.False(wording.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiNotConfigured, wording.AiReason);
        Assert.Equal(AutopilotChatText.Fallback("it", 1), wording.Texts["it-1"].Template);
        Assert.Equal(AutopilotChatText.Fallback("de", 2), wording.Texts["de-2"].Template);
        Assert.All(wording.Texts.Values, text => Assert.False(text.AiWritten));
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public async Task Write_AGoodAnswer_IsTheTextsOfTheAi()
    {
        var rig = Create();

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.True(wording.AiUsed);
        Assert.Null(wording.AiReason);
        Assert.Equal(new AutopilotWrittenText(ItalianText, true), wording.Texts["it-1"]);
        Assert.Equal(new AutopilotWrittenText(GermanText, true), wording.Texts["de-2"]);
    }

    [Fact]
    public async Task Write_WhatGoesToTheProvider_IsALanguageAndTheNumberOfTheReminder_AndNothingElse()
    {
        var rig = Create();

        await rig.Writer.WriteAsync(Org, Variants, null);

        var request = Assert.Single(rig.Requests);
        Assert.Equal("RICHIESTE\r\n<<<\r\nde-2 | tedesco | promemoria gentile\r\nit-1 | italiano | primo messaggio\r\n>>>\r\n".ReplaceLineEndings(), request.Prompt.ReplaceLineEndings());
        Assert.Empty(request.Images);
        Assert.NotNull(request.SystemPrompt);
        Assert.Contains("{link}", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_OneTextOfTwoThatDoesNotPass_IsTheOnlyOneReplacedByThePlainText()
    {
        var answer = $$"""{"it-1":"{{ItalianText}}","de-2":"Hallo {nome}, wir erwarten Sie am 12. November, bitte alle Daten eintragen. {link}"}""";
        var rig = Create(answer);

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.True(wording.AiUsed);
        Assert.Equal(new AutopilotWrittenText(ItalianText, true), wording.Texts["it-1"]);
        Assert.Equal(new AutopilotWrittenText(AutopilotChatText.Fallback("de", 2), false), wording.Texts["de-2"]);
    }

    [Theory]
    [InlineData("Non posso aiutarti con questo.")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"fr-3":"Bonjour {nome}, merci de remplir le formulaire pour enregistrer le séjour. {link}"}""")]
    [InlineData("""{"it-1":"Ciao! {link}"}""")]
    public async Task Write_AnAnswerWithNothingUsable_ThePlainTextsAndTheReason(string answer)
    {
        var rig = Create(answer);

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.False(wording.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiInvalidAnswer, wording.AiReason);
        Assert.All(wording.Texts.Values, text => Assert.False(text.AiWritten));
    }

    [Fact]
    public async Task Write_TheAnswerCanComeAfterAReasoningOrInACodeFence()
    {
        var rig = Create($"<think>due testi: {{\"it-1\":\"bozza\"}}</think>\n```json\n{GoodAnswer}\n```");

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.True(wording.AiUsed);
        Assert.Equal(ItalianText, wording.Texts["it-1"].Template);
    }

    [Fact]
    public async Task Write_TheSameVariantsAgain_AreServedFromTheCache_AndTakeNoPermit()
    {
        var rig = Create();
        var permits = 0;

        await rig.Writer.WriteAsync(Org, Variants, () => { permits++; return true; });
        var second = await rig.Writer.WriteAsync(Org, [new AutopilotChatVariant("de", 2), new AutopilotChatVariant("it", 1)], () => { permits++; return true; });

        Assert.True(second.AiUsed);
        Assert.Equal(1, permits);
        Assert.Single(rig.Requests);
    }

    [Fact]
    public async Task Write_AnotherOrgDoesNotReadTheCacheOfThisOne()
    {
        var rig = Create();

        await rig.Writer.WriteAsync(Org, Variants, null);
        await rig.Writer.WriteAsync(Guid.NewGuid(), Variants, null);

        Assert.Equal(2, rig.Requests.Count);
    }

    [Fact]
    public async Task Write_WhenTheLimitOfTheAiIsUsedUp_ThePlainTextsAreUsed_AndTheProviderIsNotCalled()
    {
        var rig = Create();

        var wording = await rig.Writer.WriteAsync(Org, Variants, () => false);

        Assert.False(wording.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiUnavailable, wording.AiReason);
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public async Task Write_WhenTheBudgetIsGone_ThePlainTextsAndTheReason()
    {
        var rig = Create(failure: new AiBudgetExceededException());

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.False(wording.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiBudgetExhausted, wording.AiReason);
    }

    [Fact]
    public async Task Write_WhenTheProviderFails_ThePlainTextsAndTheReason_NeverAnError()
    {
        var rig = Create(failure: new HttpRequestException("boom"));

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.False(wording.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiUnavailable, wording.AiReason);
        Assert.Equal(AutopilotChatText.Fallback("it", 1), wording.Texts["it-1"].Template);
    }

    [Fact]
    public async Task Write_WhenTheProviderAnswersWithAPlaceholder_ThePlainTextsAreUsed()
    {
        var rig = Create(providerConfigured: false);

        var wording = await rig.Writer.WriteAsync(Org, Variants, null);

        Assert.False(wording.AiUsed);
        Assert.Equal(AiSuggestionFallbackReasons.AiNotConfigured, wording.AiReason);
    }

    [Fact]
    public async Task Write_TheCallerLeaving_IsNotAFailureOfTheAi()
    {
        var rig = Create(failure: new OperationCanceledException());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Writer.WriteAsync(Org, Variants, null, cancelled.Token));
    }

    // ─── The prompt and the reading of the answer ────────────────────────────────

    [Fact]
    public void Parse_OfSeveralObjects_TheLastOneThatHoldsATextIsTheAnswer()
    {
        var texts = AutopilotMessagePrompt.Parse($$"""{"it-1":"bozza"} poi {"it-1":"{{ItalianText}}"}""", [new AutopilotChatVariant("it", 1)]);

        Assert.Equal(ItalianText, texts["it-1"]);
    }

    [Fact]
    public void Parse_KeysAreReadWithoutRegardToCase_AndOnesNotAskedForAreLeftOut()
    {
        var texts = AutopilotMessagePrompt.Parse($$"""{" IT-1 ":"{{ItalianText}}","de-2":"{{GermanText}}"}""", [new AutopilotChatVariant("it", 1)]);

        Assert.Equal(new[] { "it-1" }, texts.Keys);
    }

    [Fact]
    public void CacheKey_IsTheSameForTheSameVariants_WhateverTheirOrder_AndNotForOthers()
    {
        var one = AutopilotMessagePrompt.CacheKey(AutopilotMessagePrompt.Build(Variants));
        var reversed = AutopilotMessagePrompt.CacheKey(AutopilotMessagePrompt.Build(Variants.OrderByDescending(v => v.Key).ToList()));
        var other = AutopilotMessagePrompt.CacheKey(AutopilotMessagePrompt.Build([new AutopilotChatVariant("it", 3)]));

        Assert.Equal(one, reversed);
        Assert.NotEqual(one, other);
        Assert.StartsWith($"autopilot-chat:{AutopilotMessagePrompt.Version}:", one, StringComparison.Ordinal);
    }
}
