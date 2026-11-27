using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>E05/F05/US02/T01: the strict reading of the answer of the classifier. Anything unreadable or out of range is <c>Other</c> with a match of 0, never an exception.</summary>
public sealed class AssistantClassificationTests
{
    [Fact]
    public void Parse_AWellFormedAnswer_ReadsEveryField()
    {
        var classification = AssistantClassification.Parse(
            "Ecco:\n```json\n{\"situation\":\"early_arrival_or_late_checkout\",\"situationMatch\":0.8765,\"urgency\":\"High\",\"sensitiveCategories\":[\"money\",\"Personal_Data\",\"spam\"],\"intentFlags\":[\"date_change\",\"date_change\",\"Not A Flag\"],\"language\":\"EN\"}\n```");

        Assert.Equal(AssistantSituation.EarlyArrivalOrLateCheckout, classification.Situation);
        Assert.Equal(0.877m, classification.SituationMatch);
        Assert.Equal(AssistantUrgency.High, classification.Urgency);
        Assert.Equal(SensitiveCategory.Money | SensitiveCategory.PersonalData, classification.SensitiveCategories);
        Assert.Equal(["date_change"], classification.IntentFlags);
        Assert.Equal("en", classification.Language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("parking, I think")]
    [InlineData("{\"situation\":\"parking\"")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"situation\":\"parking\",\"situationMatch\":1.2}")]
    [InlineData("{\"situation\":\"parking\",\"situationMatch\":-0.1}")]
    [InlineData("{\"situation\":\"parking\",\"situationMatch\":\"high\"}")]
    [InlineData("{\"situation\":\"parking\"}")]
    [InlineData("{\"situation\":\"swimming_pool\",\"situationMatch\":0.9}")]
    [InlineData("{\"situation\":3,\"situationMatch\":0.9}")]
    [InlineData("{\"situation\":\"other\",\"situationMatch\":0.99}")]
    public void Parse_AnythingUnreadableOrOutOfRange_IsOtherWithAMatchOfZero(string? answer)
    {
        var classification = AssistantClassification.Parse(answer);

        Assert.Equal(AssistantSituation.Other, classification.Situation);
        Assert.Equal(0m, classification.SituationMatch);
    }

    [Fact]
    public void Parse_AnUnknownSituation_KeepsTheUrgencyAndTheCategoriesItCouldRead()
    {
        // An emergency the classifier could not name is still urgent and about safety: the pipeline raises its «To do» and alerts.
        var classification = AssistantClassification.Parse("{\"situation\":\"flood\",\"situationMatch\":0.9,\"urgency\":\"urgent\",\"sensitiveCategories\":[\"safety\"]}");

        Assert.Equal((AssistantSituation.Other, 0m), (classification.Situation, classification.SituationMatch));
        Assert.Equal(AssistantUrgency.Urgent, classification.Urgency);
        Assert.Equal(SensitiveCategory.Safety, classification.SensitiveCategories);
    }

    [Fact]
    public void Parse_AnUnknownUrgencyIsNormal_AndALanguageThatIsNotTwoLettersIsNone()
    {
        var classification = AssistantClassification.Parse("{\"situation\":\"WiFi\",\"situationMatch\":\"0.5\",\"urgency\":\"critical\",\"language\":\"ita\"}");

        Assert.Equal((AssistantSituation.WiFi, 0.5m), (classification.Situation, classification.SituationMatch));
        Assert.Equal(AssistantUrgency.Normal, classification.Urgency);
        Assert.Null(classification.Language);
    }

    [Fact]
    public void Parse_TheObjectAfterAReasoning_IsTheAnswer_AndAReasoningIsNeverReadAsOne()
    {
        // #936: a model that thinks aloud writes its reasoning, braces and first tries of the answer included, before the object.
        var classification = AssistantClassification.Parse(
            "<think>Maybe {\"situation\":\"wifi\",\"situationMatch\":0.4}? No: the car.</think>\n{\"situation\":\"parking\",\"situationMatch\":0.9,\"urgency\":\"normal\"} {\"note\":\"done\"}");
        var cutReasoning = AssistantClassification.Parse("<think>The guest asks about {\"situation\":\"parking\",\"situationMatch\":0.9}");

        Assert.Equal((AssistantSituation.Parking, 0.9m), (classification.Situation, classification.SituationMatch));
        Assert.Equal((AssistantSituation.Other, 0m), (cutReasoning.Situation, cutReasoning.SituationMatch));
    }
}
