using Casazen.Core.Pricing;
using Xunit;

namespace Casazen.Tests.Unit.Pricing;

/// <summary>Reading the answer of the AI to the nightly price prompt (WIZ-AI-PRICE): one JSON object, however it is dressed.</summary>
public class PriceSuggestionAnswerParserTests
{
    private const string Json = """
        {"nightlyRate": 140, "rangeMin": 120, "rangeMax": 165, "cleaningFee": 60, "weekendSurchargePercent": 15,
         "minNights": 2, "rationale": "Zona centrale e terrazza."}
        """;

    [Fact]
    public void Parse_APlainObject_ReadsEveryField()
    {
        var answer = PriceSuggestionAnswerParser.Parse(Json);

        Assert.NotNull(answer);
        Assert.Equal(140m, answer.NightlyRate);
        Assert.Equal(120m, answer.RangeMin);
        Assert.Equal(165m, answer.RangeMax);
        Assert.Equal(60m, answer.CleaningFee);
        Assert.Equal(15m, answer.WeekendSurchargePercent);
        Assert.Equal(2m, answer.MinNights);
        Assert.Equal("Zona centrale e terrazza.", answer.Rationale);
    }

    [Fact]
    public void Parse_AnObjectInACodeFenceAndASentence_IsFound()
    {
        var answer = PriceSuggestionAnswerParser.Parse($"Ecco la stima:\n```json\n{Json}\n```\nSpero sia utile.");

        Assert.NotNull(answer);
        Assert.Equal(140m, answer.NightlyRate);
    }

    [Fact]
    public void Parse_NumbersWrittenAsText_AreRead()
    {
        var answer = PriceSuggestionAnswerParser.Parse(
            """{"nightlyRate": "140 €", "rangeMin": "120,5", "weekendSurchargePercent": "15%", "minNights": "2"}""");

        Assert.NotNull(answer);
        Assert.Equal(140m, answer.NightlyRate);
        Assert.Equal(120.5m, answer.RangeMin);
        Assert.Equal(15m, answer.WeekendSurchargePercent);
        Assert.Equal(2m, answer.MinNights);
    }

    [Fact]
    public void Parse_NamesInAnyCase_TrailingCommasAndUnknownMembers_AreAccepted()
    {
        var answer = PriceSuggestionAnswerParser.Parse("""{"NightlyRate": 150, "currency": "EUR", "Rationale": "Ok",}""");

        Assert.NotNull(answer);
        Assert.Equal(150m, answer.NightlyRate);
        Assert.Equal("Ok", answer.Rationale);
        Assert.Null(answer.CleaningFee);
    }

    [Fact]
    public void Parse_ANumberThatIsNotOne_IsLeftOut()
    {
        var answer = PriceSuggestionAnswerParser.Parse("""{"nightlyRate": 150, "cleaningFee": "circa sessanta", "minNights": [2]}""");

        Assert.NotNull(answer);
        Assert.Null(answer.CleaningFee);
        Assert.Null(answer.MinNights);
    }

    // ─── A model that reasons aloud ────────────────────────────────────────────────

    [Fact]
    public void Parse_AReasoningBeforeTheAnswer_IsNotTheAnswer()
    {
        var answer = PriceSuggestionAnswerParser.Parse(
            "<think>Proviamo con {\"nightlyRate\": 999, \"rationale\": \"bozza\"} oppure meno.</think>\n\n" + Json);

        Assert.NotNull(answer);
        Assert.Equal(140m, answer.NightlyRate);
        Assert.Equal("Zona centrale e terrazza.", answer.Rationale);
    }

    [Fact]
    public void Parse_OnlyTheClosingTagOfAReasoning_CutsEverythingBeforeIt()
    {
        var answer = PriceSuggestionAnswerParser.Parse("Bozza: {\"nightlyRate\": 999}\nOk, ci siamo.\n</think>\n{\"nightlyRate\": 150}");

        Assert.NotNull(answer);
        Assert.Equal(150m, answer.NightlyRate);
    }

    [Fact]
    public void Parse_AReasoningThatWasNeverClosed_HasNoAnswer()
    {
        // The model ran out of room while thinking: a draft inside the reasoning is not an answer.
        Assert.Null(PriceSuggestionAnswerParser.Parse("<think>Forse {\"nightlyRate\": 140} ma controllo ancora"));
    }

    [Fact]
    public void Parse_SeveralObjects_TheLastOneWithARateIsTheAnswer()
    {
        var answer = PriceSuggestionAnswerParser.Parse("""{"nightlyRate": 100} e poi, corretto: {"nightlyRate": 120, "minNights": 2} {"nota": "fine"}""");

        Assert.NotNull(answer);
        Assert.Equal(120m, answer.NightlyRate);
        Assert.Equal(2m, answer.MinNights);
    }

    [Fact]
    public void Parse_BracesInsideTheExplanation_DoNotBreakTheObject()
    {
        var answer = PriceSuggestionAnswerParser.Parse("""{"nightlyRate": 140, "rationale": "Zona {centro} e terrazza, \"molto\" richiesta."}""");

        Assert.NotNull(answer);
        Assert.Equal(140m, answer.NightlyRate);
        Assert.Equal("Zona {centro} e terrazza, \"molto\" richiesta.", answer.Rationale);
    }

    [Fact]
    public void Parse_AStrayBraceInTheProse_IsSkipped()
    {
        var answer = PriceSuggestionAnswerParser.Parse("""Ecco { la stima: {"nightlyRate": 130}""");

        Assert.NotNull(answer);
        Assert.Equal(130m, answer.NightlyRate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Non so che prezzo suggerire.")]
    [InlineData("{ non è json }")]
    [InlineData("""{"rangeMin": 100, "rangeMax": 200}""")]
    [InlineData("""{"nightlyRate": "boh"}""")]
    [InlineData("""["nightlyRate", 140]""")]
    [InlineData("""{"nightlyRate": 140""")]
    [InlineData("</think>")]
    public void Parse_AnythingWithoutARate_IsNull(string? content)
    {
        Assert.Null(PriceSuggestionAnswerParser.Parse(content));
    }
}
