using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02 (NF-66): what an organization or a property may save as the rules of its assistant. A bad value is a 422 with a code of its own, an empty list is never «all»,
/// and the names of the topics are the ones the assistant knows.
/// </summary>
public class AssistantRulesValidatorTests
{
    private static string CodeOf(Action action) => Assert.Throws<DomainRuleException>(action).Code;

    [Fact]
    public void CleanLanguages_AtLeastOne_EachTwoLowerCaseLetters_NoneTwice()
    {
        Assert.Equal(["it", "en", "de"], AssistantRulesValidator.CleanLanguages(["it", "en", "it", "de"]));

        // No language at all is not «every language»: it is refused, and so is a missing list.
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanLanguages([])));
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanLanguages(null)));
    }

    [Theory]
    [InlineData("ita")]
    [InlineData("i")]
    [InlineData("IT")]
    [InlineData("it ")]
    [InlineData("it\n")]
    [InlineData("1t")]
    [InlineData("")]
    public void CleanLanguages_ACodeThatIsNotTwoLowerCaseLetters_Is422(string code) =>
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanLanguages(["it", code])));

    [Fact]
    public void CleanSignature_IsTrimmed_EmptyIsNone_AndUpTo300Characters()
    {
        Assert.Equal("Marco, Casa Rossi", AssistantRulesValidator.CleanSignature("  Marco, Casa Rossi \n"));
        Assert.Null(AssistantRulesValidator.CleanSignature("   "));
        Assert.Null(AssistantRulesValidator.CleanSignature(null));

        Assert.Equal(300, AssistantRulesValidator.CleanSignature(new string('a', OrgAssistantRules.SignatureMaxLength))!.Length);
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanSignature(new string('a', 301))));
    }

    [Fact]
    public void CleanHours_BothOrNeither_AStartDifferentFromTheEnd_AWindowAcrossMidnightIsAllowed()
    {
        Assert.Equal((null, null), AssistantRulesValidator.CleanHours(null, null));
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(21, 30)), AssistantRulesValidator.CleanHours(new TimeOnly(9, 0), new TimeOnly(21, 30)));

        // 22:00 to 06:00 crosses midnight: allowed.
        Assert.Equal((new TimeOnly(22, 0), new TimeOnly(6, 0)), AssistantRulesValidator.CleanHours(new TimeOnly(22, 0), new TimeOnly(6, 0)));

        // Whole minutes: the seconds of a client are dropped, and a start equal to the end after that is refused.
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(21, 30)), AssistantRulesValidator.CleanHours(new TimeOnly(9, 0, 45), new TimeOnly(21, 30, 10)));
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanHours(new TimeOnly(9, 0, 5), new TimeOnly(9, 0, 50))));

        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanHours(new TimeOnly(9, 0), new TimeOnly(9, 0))));
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanHours(new TimeOnly(9, 0), null)));
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanHours(null, new TimeOnly(9, 0))));
    }

    [Theory]
    [InlineData("0.50")]
    [InlineData("0.85")]
    [InlineData("0.855")]
    [InlineData("0.99")]
    public void CleanThreshold_FromHalfToNinetyNine_WithAtMostThreeDecimals_IsKept(string value) =>
        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), AssistantRulesValidator.CleanThreshold(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0.49")]
    [InlineData("0.499")]
    [InlineData("0.991")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-0.9")]
    [InlineData("0.8555")]
    public void CleanThreshold_OutsideTheRangeOrWithMoreThanThreeDecimals_Is422WithItsOwnCode(string value)
    {
        var threshold = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(AssistantRulesValidator.ThresholdInvalidCode, CodeOf(() => AssistantRulesValidator.CleanThreshold(threshold)));
        Assert.Equal(AssistantRulesValidator.ThresholdInvalidCode, CodeOf(() => AssistantRulesValidator.CleanOptionalThreshold(threshold)));
    }

    [Fact]
    public void CleanThreshold_NotGiven_IsTheDefault_AndForAPropertyItIsNull()
    {
        Assert.Equal(0.85m, AssistantRulesValidator.CleanThreshold(null));
        Assert.Null(AssistantRulesValidator.CleanOptionalThreshold(null));
    }

    [Fact]
    public void CleanTopics_AreNamesTheAssistantKnows_InAnyCase_SavedWithTheSpellingOfTheEnums_NoneTwice()
    {
        Assert.Equal(
            ["Parking", "InvoiceRequest", "WiFi"],
            AssistantRulesValidator.CleanTopics(["parking", "INVOICEREQUEST", "Parking", " wifi "]));

        // The stable code of a situation, which the clients use as the key of its label, is accepted too and saved as the name of the member.
        Assert.Equal(
            ["CheckInTime", "CancellationOrRefund", "Parking"],
            AssistantRulesValidator.CleanTopics(["check_in_time", "CANCELLATION_OR_REFUND", "parking"]));

        // No list is the empty list: no restriction by an allowed list, nothing forbidden. Never «every topic».
        Assert.Empty(AssistantRulesValidator.CleanTopics(null));
        Assert.Empty(AssistantRulesValidator.CleanTopics([]));
    }

    [Theory]
    [InlineData("Gossip")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("7")]
    [InlineData("Parking;Pets")]
    public void CleanTopics_ANameTheAssistantDoesNotKnow_Is422(string name) =>
        Assert.Equal(AssistantRulesValidator.RulesInvalidCode, CodeOf(() => AssistantRulesValidator.CleanTopics(["Parking", name])));

    [Fact]
    public void KnownTopicNames_AreTheTopicsAndTheSituations_EachOnce()
    {
        Assert.Equal(Known.Distinct(StringComparer.Ordinal).Count(), Known.Count);
        Assert.Contains("CheckIn", Known);
        Assert.Contains("InvoiceRequest", Known);
        Assert.Contains("Other", Known);
        Assert.Contains("Faq", Known);

        Assert.True(AssistantTopicNames.IsListed(["Parking"], AssistantSituation.Parking, null));
        Assert.True(AssistantTopicNames.IsListed(["parking"], null, KnowledgeTopic.Parking));
        Assert.False(AssistantTopicNames.IsListed([], AssistantSituation.Parking, KnowledgeTopic.Parking));
        Assert.False(AssistantTopicNames.IsListed(["Pets"], AssistantSituation.Parking, null));
        Assert.False(AssistantTopicNames.IsListed(["Parking"], null, null));
    }

    private static IReadOnlyList<string> Known => AssistantTopicNames.Known;
}
