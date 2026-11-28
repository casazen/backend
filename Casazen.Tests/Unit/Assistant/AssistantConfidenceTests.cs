using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

public sealed class AssistantConfidenceTests
{
    private static readonly EffectiveAssistantRules Rules =
        EffectiveAssistantRules.Defaults with { Languages = ["it", "en"], Signature = "Marco", ForbiddenTopics = ["DiscountRequest"] };

    private const string Body = "Ciao Giulia, il parcheggio è nel cortile.";

    [Fact]
    public void Compute_IsTheProductOfTheThreeComponents_RoundedToThreeDecimals()
    {
        var score = AssistantConfidence.Compute(Input(AssistantSituation.EarlyArrivalOrLateCheckout, 0.876m, covered: [KnowledgeTopic.CheckIn]));

        Assert.Equal((0.876m, 0.5m, 1m), (score.SituationMatch, score.FactCoverage, score.RuleCompliance));
        Assert.Equal(0.438m, score.Score);
        Assert.Empty(score.BrokenRules);
    }

    [Fact]
    public void FactCoverage_IsOneWhenNothingIsRequired_AndHalvedWhenTheModelSaysAFactWasMissing()
    {
        Assert.Equal(1m, AssistantConfidence.FactCoverageOf(AssistantSituation.ThankYou, [], missingFacts: false));
        Assert.Equal(0.5m, AssistantConfidence.FactCoverageOf(AssistantSituation.ThankYou, [], missingFacts: true));
        Assert.Equal(0m, AssistantConfidence.FactCoverageOf(AssistantSituation.Parking, [KnowledgeTopic.WiFi], missingFacts: false));
    }

    [Fact]
    public void Other_HasNoMatch_SoItsScoreIsZero()
    {
        var score = AssistantConfidence.Compute(Input(AssistantSituation.Other, 0.99m, covered: []));

        Assert.Equal((0m, 0m), (score.SituationMatch, score.Score));
    }

    [Fact]
    public void EachSoftRuleBroken_TakesAQuarterOff()
    {
        var longBody = new string('a', AssistantConfidence.DraftMaxLength + 1);
        var score = AssistantConfidence.Compute(Input(
            AssistantSituation.Parking,
            1m,
            covered: [KnowledgeTopic.Parking],
            body: longBody,
            final: longBody + " Scrivimi a host@example.com, sono un assistente virtuale.",
            topics: ["DiscountRequest"],
            language: "de"));

        Assert.Equal(
            [
                AssistantConfidence.TooLongRule, AssistantConfidence.ForbiddenTopicRule, AssistantConfidence.ContactDetailRule,
                AssistantConfidence.LanguageRule, AssistantConfidence.SignatureRule, AssistantConfidence.AiDisclosureRule,
            ],
            score.BrokenRules);
        Assert.Equal(0m, score.RuleCompliance);

        var one = AssistantConfidence.Compute(Input(AssistantSituation.Parking, 1m, covered: [KnowledgeTopic.Parking], language: "fr"));
        Assert.Equal((0.75m, 0.75m), (one.RuleCompliance, one.Score));
    }

    [Fact]
    public void AContactDetailOfTheFacts_BreaksNoRule_ButAPlaceholderOfARemovedDatumDoes()
    {
        var facts = new List<string> { "Per emergenze chiamate il 347 9876543." };

        Assert.False(AssistantConfidence.HasForeignContactDetail("Per emergenze chiama il 3479876543. Marco", facts));
        Assert.True(AssistantConfidence.HasForeignContactDetail("Per emergenze chiama il [tel]. Marco", facts));
        Assert.True(AssistantConfidence.HasForeignContactDetail("Guarda www.altrosito.it. Marco", facts));
    }

    [Fact]
    public void TheChannelRefusingTheText_IsACompliance0_WhateverElse()
    {
        var score = AssistantConfidence.Compute(Input(AssistantSituation.Parking, 1m, covered: [KnowledgeTopic.Parking], refusal: MessageChannelRules.ContactDetailsCode));

        Assert.Equal((0m, 0m), (score.RuleCompliance, score.Score));
        Assert.Contains(AssistantConfidence.ChannelRule, score.BrokenRules);
    }

    [Theory]
    [InlineData("Sono un assistente virtuale e ti rispondo subito", true)]
    [InlineData("As an AI, I cannot check this", true)]
    [InlineData("Questa è una risposta automatica", true)]
    [InlineData("Ciao, ti aspettiamo alle 15! Marco", false)]
    [InlineData("Air conditioning is available", false)]
    public void DisclosesAi_FindsTheWordsThatSayAMachineWrote(string text, bool discloses) =>
        Assert.Equal(discloses, AssistantConfidence.DisclosesAi(text));

    private static AssistantConfidenceInput Input(
        AssistantSituation situation,
        decimal match,
        IReadOnlyCollection<KnowledgeTopic> covered,
        string body = Body,
        string? final = null,
        IReadOnlyList<string>? topics = null,
        string? language = "it",
        string? refusal = null) =>
        new(situation, match, covered, false, body, final ?? $"{body}\n\nMarco", ["Il parcheggio è nel cortile."], topics ?? [], language, Rules, refusal);
}
