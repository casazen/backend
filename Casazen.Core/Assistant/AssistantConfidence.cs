using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Assistant;

/// <summary>What the confidence of a draft is computed from (<see cref="AssistantConfidence.Compute"/>).</summary>
/// <param name="Situation">The situation the classifier recognized.</param>
/// <param name="SituationMatch">How well the message matched it, as the classifier reported (0 to 1).</param>
/// <param name="CoveredTopics">The topics that have at least one retrieved fact.</param>
/// <param name="MissingFacts">The model reported that a fact the answer needed was missing.</param>
/// <param name="Body">The draft without the signature: what the length rule measures.</param>
/// <param name="FinalText">The text the guest would read: the body with the variables filled in and the signature.</param>
/// <param name="FactTexts">The facts as the knowledge base holds them: a link or a contact detail of the draft has to be one of theirs.</param>
/// <param name="DraftTopics">The topics the model says the draft talks about (canonical names); none for a template.</param>
/// <param name="DraftLanguage">The language the draft is written in; <c>null</c> when not known (a template).</param>
/// <param name="Rules">The rules in force for the property.</param>
/// <param name="ChannelRefusal">The code of the rule of the channel that refuses the text (<see cref="MessageChannelRules.CheckOutgoing"/>), or <c>null</c> when it may go out.</param>
public sealed record AssistantConfidenceInput(
    AssistantSituation Situation,
    decimal SituationMatch,
    IReadOnlyCollection<KnowledgeTopic> CoveredTopics,
    bool MissingFacts,
    string Body,
    string FinalText,
    IReadOnlyList<string> FactTexts,
    IReadOnlyList<string> DraftTopics,
    string? DraftLanguage,
    EffectiveAssistantRules Rules,
    string? ChannelRefusal);

/// <summary>The confidence of a draft: the score and its three components, each 0 to 1 with three decimals, and the codes of the rules the draft broke.</summary>
public sealed record AssistantConfidenceScore(decimal Score, decimal SituationMatch, decimal FactCoverage, decimal RuleCompliance, IReadOnlyList<string> BrokenRules);

/// <summary>
/// How sure the assistant is of a draft (E05/F05/US02/T01, NF-67), pure: <c>score = round(situationMatch × factCoverage × ruleCompliance, 3)</c>. <b>The product of the three
/// components is a proposal for the product owner to confirm</b> (the auto-send gate compares the score with the threshold of the rules).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>situationMatch</b>: as the classifier reported it, 0 for <see cref="AssistantSituation.Other"/>.</item>
/// <item><b>factCoverage</b>: the topics the situation requires (<see cref="AssistantSituationInfo.RequiredTopics"/>) that have at least one retrieved fact, over the topics it requires;
/// 1 when it requires none; halved when the model reports that a fact was missing.</item>
/// <item><b>ruleCompliance</b>: 1 minus <see cref="SoftRulePenalty"/> for each soft rule broken (never below 0): over <see cref="DraftMaxLength"/> characters, a forbidden topic, a link,
/// a contact detail or a placeholder of a removed datum that is not in the facts, a language the rules do not allow, the signature missing, a phrase that discloses an AI. <b>0 when
/// the rules of the channel refuse the text</b>.</item>
/// </list>
/// </remarks>
public static partial class AssistantConfidence
{
    /// <summary>The longest draft that breaks no rule, in characters (the signature is not counted).</summary>
    public const int DraftMaxLength = 800;

    /// <summary>What each soft rule broken takes off the compliance.</summary>
    public const decimal SoftRulePenalty = 0.25m;

    public const string TooLongRule = "draft_too_long";
    public const string ForbiddenTopicRule = "forbidden_topic";
    public const string ContactDetailRule = "contact_detail_not_in_facts";
    public const string LanguageRule = "language_not_allowed";
    public const string SignatureRule = "signature_missing";
    public const string AiDisclosureRule = "ai_disclosed";

    /// <summary>The hard rule: the channel refuses the text (the compliance is 0).</summary>
    public const string ChannelRule = "channel_refused";

    /// <summary>The score of a draft and its components (see the remarks of the class).</summary>
    public static AssistantConfidenceScore Compute(AssistantConfidenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var match = input.Situation == AssistantSituation.Other ? 0m : Clamp(input.SituationMatch);
        var coverage = FactCoverageOf(input.Situation, input.CoveredTopics, input.MissingFacts);
        var broken = BrokenRulesOf(input);
        var compliance = broken.Contains(ChannelRule, StringComparer.Ordinal)
            ? 0m
            : Math.Max(0m, 1m - (SoftRulePenalty * broken.Count));

        return new AssistantConfidenceScore(Round(match * coverage * compliance), Round(match), Round(coverage), Round(compliance), broken);
    }

    /// <summary>The topics the situation requires that have a fact, over the topics it requires; 1 when it requires none; halved when a fact was reported missing.</summary>
    public static decimal FactCoverageOf(AssistantSituation situation, IReadOnlyCollection<KnowledgeTopic> covered, bool missingFacts)
    {
        ArgumentNullException.ThrowIfNull(covered);

        var required = AssistantSituationCatalog.Of(situation).RequiredTopics;
        var coverage = required.Count == 0 ? 1m : (decimal)required.Count(topic => covered.Contains(topic)) / required.Count;
        return missingFacts ? coverage / 2m : coverage;
    }

    /// <summary>The codes of the rules <paramref name="input"/> breaks: the soft ones, and <see cref="ChannelRule"/> when the channel refuses the text.</summary>
    public static IReadOnlyList<string> BrokenRulesOf(AssistantConfidenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var broken = new List<string>();
        if (input.Body.Length > DraftMaxLength)
            broken.Add(TooLongRule);

        if (input.DraftTopics.Any(topic => input.Rules.ForbiddenTopics.Contains(topic, StringComparer.OrdinalIgnoreCase)))
            broken.Add(ForbiddenTopicRule);

        if (HasForeignContactDetail(input.FinalText, input.FactTexts))
            broken.Add(ContactDetailRule);

        if (input.DraftLanguage is { } language && !input.Rules.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            broken.Add(LanguageRule);

        if (input.Rules.Signature?.Trim() is { Length: > 0 } signature && !input.FinalText.TrimEnd().EndsWith(signature, StringComparison.Ordinal))
            broken.Add(SignatureRule);

        if (DisclosesAi(input.FinalText))
            broken.Add(AiDisclosureRule);

        if (input.ChannelRefusal is not null)
            broken.Add(ChannelRule);

        return broken;
    }

    /// <summary>
    /// True when <paramref name="text"/> holds a link, an e-mail address or a phone number that none of <paramref name="facts"/> holds, or a placeholder of a datum that was taken out
    /// before the call (<see cref="AssistantPromptBuilder.Placeholders"/>): the model was told to write neither.
    /// </summary>
    public static bool HasForeignContactDetail(string text, IReadOnlyList<string> facts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(facts);

        if (AssistantPromptBuilder.PlaceholdersIn(text).Count > 0)
            return true;

        return MessageChannelRules.ContactDetailsIn(text)
            .Any(detail => !facts.Any(fact => Compact(fact).Contains(Compact(detail), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when <paramref name="text"/> says, in Italian or in English, that it was written by an AI, a bot or an automatic assistant.</summary>
    public static bool DisclosesAi(string? text) => !string.IsNullOrEmpty(text) && AiDisclosure().IsMatch(text);

    private static decimal Clamp(decimal value) => Math.Min(1m, Math.Max(0m, value));

    private static decimal Round(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    /// <summary>A text without its spaces, so a number written «333 123 4567» is found in a fact that writes it «3331234567».</summary>
    private static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    // The words of a message that tell the guest it was written by a machine, in Italian and in English.
    [GeneratedRegex(
        @"\b(?:intelligenza\s+artificiale|assistente\s+(?:virtuale|automatico|digitale)|risposta\s+automatica|messaggio\s+automatico|modello\s+linguistico|sono\s+un\s+(?:bot|robot|programma)"
        + @"|artificial\s+intelligence|virtual\s+assistant|language\s+model|automated\s+(?:reply|response|message|assistant)|as\s+an\s+ai|i\s+am\s+an\s+ai|i'?m\s+an\s+ai"
        + @"|chat\s?bot|chatgpt|gpt-?\d|ai\s+assistant|assistente\s+ai|assistente\s+ia)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AiDisclosure();
}
