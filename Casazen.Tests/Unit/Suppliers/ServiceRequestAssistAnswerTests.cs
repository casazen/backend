using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// The answer of the AI to «Descrivi di cosa hai bisogno» (WIZ-AI-REQUEST): how it is read (an object, maybe in a code fence or after a reasoning,
/// the last one with a category) and what is checked before the host sees it (a category of the platform, an urgency of the enum, plain short texts).
/// </summary>
public class ServiceRequestAssistAnswerTests
{
    private const string Good =
        """{"category":"plumbing","urgency":"High","summary":"Il rubinetto della cucina perde acqua.","reason":"Una perdita è un lavoro da idraulico."}""";

    // ─── Parse ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_AnObject_ReadsItsMembers()
    {
        var raw = ServiceRequestAssistAnswerParser.Parse(Good);

        Assert.NotNull(raw);
        Assert.Equal("plumbing", raw.Category);
        Assert.Equal("High", raw.Urgency);
        Assert.Equal("Il rubinetto della cucina perde acqua.", raw.Summary);
        Assert.Equal("Una perdita è un lavoro da idraulico.", raw.Reason);
    }

    [Fact]
    public void Parse_InACodeFenceAndAfterASentence_IsStillRead()
    {
        var raw = ServiceRequestAssistAnswerParser.Parse($"Ecco la risposta:\n```json\n{Good}\n```");

        Assert.Equal("plumbing", raw?.Category);
    }

    [Fact]
    public void Parse_NamesAreReadWithoutRegardToCase_AndExplanationIsTheReason()
    {
        var raw = ServiceRequestAssistAnswerParser.Parse("""{"Category":"cleaning","URGENCY":"Normal","Summary":"Pulizia.","Explanation":"Serve una pulizia."}""");

        Assert.NotNull(raw);
        Assert.Equal("cleaning", raw.Category);
        Assert.Equal("Normal", raw.Urgency);
        Assert.Equal("Pulizia.", raw.Summary);
        Assert.Equal("Serve una pulizia.", raw.Reason);
    }

    [Fact]
    public void Parse_AModelThatReasonsAloud_IsReadAfterTheReasoning()
    {
        var raw = ServiceRequestAssistAnswerParser.Parse(
            """<think>Forse {"category":"cleaning"}? No, è una perdita: {"category":"maintenance"}</think>{"category":"plumbing","urgency":"High"}""");

        Assert.Equal("plumbing", raw?.Category);
    }

    [Fact]
    public void Parse_OfSeveralObjects_TheLastOneWithACategoryIsTheAnswer()
    {
        var raw = ServiceRequestAssistAnswerParser.Parse(
            """{"category":"cleaning","urgency":"Normal"} Meglio così: {"category":"electrical","urgency":"Emergency"} {"note":"fine"}""");

        Assert.Equal("electrical", raw?.Category);
        Assert.Equal("Emergency", raw?.Urgency);
    }

    [Fact]
    public void Parse_AnExplicitNullCategory_IsAnAnswer_ThatSaysThereIsNoCategory()
    {
        var raw = ServiceRequestAssistAnswerParser.Parse("""{"category":null,"urgency":"Normal","summary":"","reason":"Non è chiaro."}""");

        Assert.NotNull(raw);
        Assert.Null(raw.Category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Non so cosa dire.")]
    [InlineData("""{"urgency":"High","summary":"Manca la categoria."}""")]
    [InlineData("""{"category":42}""")]
    [InlineData("""{"category":["plumbing"]}""")]
    [InlineData("""{"category":"plumbing" """)]
    [InlineData("""<think>{"category":"plumbing"}""")]
    public void Parse_AnythingElse_IsNotAnAnswer(string? content)
    {
        Assert.Null(ServiceRequestAssistAnswerParser.Parse(content));
    }

    // ─── Check ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Check_AKnownCategory_IsAcceptedWithItsUrgencyAndTexts()
    {
        var (answer, reason) = ServiceRequestAssistRules.Check(new RawAssistAnswer(" Plumbing ", "High", "Il rubinetto perde.", "È un lavoro da idraulico."));

        Assert.Null(reason);
        Assert.NotNull(answer);
        Assert.Equal(ServiceCategories.Plumbing, answer.Category);
        Assert.Equal(ServiceRequestUrgency.High, answer.Urgency);
        Assert.Equal("Il rubinetto perde.", answer.Summary);
        Assert.Equal("È un lavoro da idraulico.", answer.Explanation);
    }

    [Theory]
    [InlineData("Normal", ServiceRequestUrgency.Normal)]
    [InlineData("high", ServiceRequestUrgency.High)]
    [InlineData(" EMERGENCY ", ServiceRequestUrgency.Emergency)]
    [InlineData("urgente", ServiceRequestUrgency.Normal)]
    [InlineData("1", ServiceRequestUrgency.Normal)]
    [InlineData("2", ServiceRequestUrgency.Normal)]
    [InlineData("", ServiceRequestUrgency.Normal)]
    [InlineData(null, ServiceRequestUrgency.Normal)]
    public void Check_TheUrgencyIsOneOfTheThreeByName_AndNormalOtherwise(string? urgency, ServiceRequestUrgency expected)
    {
        var (answer, _) = ServiceRequestAssistRules.Check(new RawAssistAnswer("cleaning", urgency, null, null));

        Assert.Equal(expected, answer?.Urgency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Check_NoCategory_IsAJobTheAiCouldNotTell(string? category)
    {
        var (answer, reason) = ServiceRequestAssistRules.Check(new RawAssistAnswer(category, "Normal", "x", "y"));

        Assert.Null(answer);
        Assert.Equal(ServiceRequestAssistReasons.NotUnderstood, reason);
    }

    [Theory]
    [InlineData("pulizie")]
    [InlineData("pool")]
    [InlineData("cleaning,plumbing")]
    [InlineData("check_in")]
    public void Check_ACategoryThatIsNotOneOfThePlatform_IsAnInvalidAnswer(string category)
    {
        var (answer, reason) = ServiceRequestAssistRules.Check(new RawAssistAnswer(category, "Normal", "x", "y"));

        Assert.Null(answer);
        Assert.Equal(AiSuggestionFallbackReasons.AiInvalidAnswer, reason);
    }

    [Fact]
    public void Check_EveryCategoryOfThePlatform_CanBeChosen()
    {
        foreach (var code in ServiceCategories.All)
        {
            var (answer, reason) = ServiceRequestAssistRules.Check(new RawAssistAnswer(code, "Normal", null, null));

            Assert.Null(reason);
            Assert.Equal(code, answer?.Category);
        }
    }

    [Fact]
    public void Check_TheTextsAreOneLineEach_AndCutAtTheirLimits()
    {
        var long1 = string.Join(' ', Enumerable.Repeat("Il rubinetto della cucina perde acqua dal sifone.", 12));
        var (answer, _) = ServiceRequestAssistRules.Check(new RawAssistAnswer("plumbing", "Normal", $"Riga uno\nriga due\t con tab.  {long1}", long1));

        Assert.NotNull(answer);
        Assert.DoesNotContain("\n", answer.Summary);
        Assert.DoesNotContain("\t", answer.Summary);
        Assert.DoesNotContain("  ", answer.Summary);
        Assert.StartsWith("Riga uno riga due con tab.", answer.Summary);
        Assert.True(answer.Summary!.Length <= ServiceRequestAssistLimits.MaxSummaryLength);
        Assert.True(answer.Explanation!.Length <= ServiceRequestAssistLimits.MaxExplanationLength);
        // Cut where a sentence ends when one does fit in the second half of the room.
        Assert.EndsWith(".", answer.Summary);
        Assert.EndsWith(".", answer.Explanation);
    }

    [Fact]
    public void Check_EmptyTexts_AreNull_NotEmptyStrings()
    {
        var (answer, _) = ServiceRequestAssistRules.Check(new RawAssistAnswer("cleaning", "Normal", "  \n ", ""));

        Assert.NotNull(answer);
        Assert.Null(answer.Summary);
        Assert.Null(answer.Explanation);
    }

    // ─── OneLine ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public void OneLine_NothingButWhiteSpace_IsNull(string? text)
    {
        Assert.Null(ServiceRequestAssistRules.OneLine(text, 50));
    }

    [Fact]
    public void OneLine_ControlCharactersAndRunsOfSpaceBecomeASingleSpace()
    {
        Assert.Equal("a b c d", ServiceRequestAssistRules.OneLine("  a\u0001b \r\n c\t\td  ", 50));
    }

    [Fact]
    public void OneLine_WithoutASentenceEnd_IsCutAtTheLastWord()
    {
        var cut = ServiceRequestAssistRules.OneLine("uno due tre quattro cinque sei sette otto nove dieci", 30);

        Assert.Equal("uno due tre quattro cinque sei", cut);
    }

    [Fact]
    public void OneLine_WithoutASpaceAtAll_IsCutWhereTheLimitIs()
    {
        Assert.Equal(new string('x', 10), ServiceRequestAssistRules.OneLine(new string('x', 40), 10));
    }
}
