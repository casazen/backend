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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Non so che prezzo suggerire.")]
    [InlineData("{ non è json }")]
    [InlineData("""{"rangeMin": 100, "rangeMax": 200}""")]
    [InlineData("""{"nightlyRate": "boh"}""")]
    [InlineData("""["nightlyRate", 140]""")]
    public void Parse_AnythingWithoutARate_IsNull(string? content)
    {
        Assert.Null(PriceSuggestionAnswerParser.Parse(content));
    }
}
