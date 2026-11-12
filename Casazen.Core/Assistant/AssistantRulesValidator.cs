using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Assistant;

/// <summary>
/// The validation of the rules of the assistant (E05/F05/US01/T02, NF-66), pure: what an organization or a property may save, checked before anything is written. A value
/// that breaks a rule is a 422 with a code of its own and nothing is saved: <c>assistant_rules_invalid</c> (languages, signature, hours, topics) or
/// <c>assistant_threshold_invalid</c> (the confidence threshold).
/// </summary>
/// <remarks>
/// <b>No empty list widens a reach.</b> No endpoint of the assistant takes a list of properties or ids, so there is no «empty = every property». The languages are at least one (an
/// empty list is 422, never «every language»). The lists of topics are optional filters that have to name topics the assistant knows: no allowed list is the state of an organization
/// that never saved its rules (no restriction by it), an empty forbidden list forbids nothing, and neither lets a sensitive situation go automatic (the rule of the modes,
/// <see cref="AssistantModeRules"/>, does not read them).
/// </remarks>
public static partial class AssistantRulesValidator
{
    /// <summary>The 422 of a language, a signature, an hours window or a topic that is not valid.</summary>
    public const string RulesInvalidCode = "assistant_rules_invalid";

    /// <summary>The 422 of a confidence threshold outside 0.50 to 0.99 or with more than three decimals.</summary>
    public const string ThresholdInvalidCode = "assistant_threshold_invalid";

    /// <summary>
    /// The languages to save: at least one, each a two-letter lower-case code (<c>it</c>, <c>en</c>), none twice, in the order given. 422
    /// <see cref="RulesInvalidCode"/> for none at all or for a code that is not one.
    /// </summary>
    public static List<string> CleanLanguages(IReadOnlyList<string>? languages)
    {
        if (languages is null || languages.Count == 0)
            throw Invalid();

        var clean = new List<string>(languages.Count);
        foreach (var language in languages)
        {
            if (language is null || !LanguageCode().IsMatch(language))
                throw Invalid();

            if (!clean.Contains(language, StringComparer.Ordinal))
                clean.Add(language);
        }

        return clean;
    }

    /// <summary>The signature to save: trimmed and normalized like a message, <c>null</c> when empty; 422 <see cref="RulesInvalidCode"/> over 300 characters.</summary>
    public static string? CleanSignature(string? signature)
    {
        var clean = GuestMessageRules.NormalizeText(signature);
        if (clean is not null && clean.Length > OrgAssistantRules.SignatureMaxLength)
            throw Invalid();

        return clean;
    }

    /// <summary>
    /// The hours to save, whole minutes: both ends or neither (<c>null</c> = at any time), and a start different from the end. A window that crosses midnight
    /// (<c>22:00</c> to <c>06:00</c>) is allowed. 422 <see cref="RulesInvalidCode"/> otherwise.
    /// </summary>
    public static (TimeOnly? From, TimeOnly? To) CleanHours(TimeOnly? from, TimeOnly? to)
    {
        if (from is null && to is null)
            return (null, null);

        if (from is null || to is null)
            throw Invalid();

        var start = new TimeOnly(from.Value.Hour, from.Value.Minute);
        var end = new TimeOnly(to.Value.Hour, to.Value.Minute);
        if (start == end)
            throw Invalid();

        return (start, end);
    }

    /// <summary>The threshold to save: 0.50 to 0.99 with at most three decimals (the column is <c>numeric(4,3)</c>); a threshold that is not given is the default, 0.85.</summary>
    public static decimal CleanThreshold(decimal? threshold) => threshold is null ? OrgAssistantRules.DefaultConfidenceThreshold : Checked(threshold.Value);

    /// <summary>The threshold of a property: not given = as the organization (<c>null</c>); given, the same rule as <see cref="CleanThreshold"/>.</summary>
    public static decimal? CleanOptionalThreshold(decimal? threshold) => threshold is null ? null : Checked(threshold.Value);

    /// <summary>
    /// The topics to save: names of <see cref="AssistantTopicNames.Known"/> (any case), saved with the spelling of the enums, none twice. No list is the empty list. 422
    /// <see cref="RulesInvalidCode"/> for a name the assistant does not know.
    /// </summary>
    public static List<string> CleanTopics(IReadOnlyList<string>? topics)
    {
        var clean = new List<string>();
        foreach (var topic in topics ?? [])
        {
            if (!AssistantTopicNames.TryNormalize(topic, out var name))
                throw Invalid();

            if (!clean.Contains(name, StringComparer.Ordinal))
                clean.Add(name);
        }

        return clean;
    }

    private static decimal Checked(decimal value)
    {
        if (value < OrgAssistantRules.MinConfidenceThreshold
            || value > OrgAssistantRules.MaxConfidenceThreshold
            || decimal.Round(value, 3) != value)
        {
            throw new DomainRuleException(ThresholdInvalidCode, "AssistantThresholdInvalid");
        }

        return value;
    }

    private static DomainRuleException Invalid() => new(RulesInvalidCode, "AssistantRulesInvalid");

    // A language of the assistant is a two-letter lower-case code: it, en, de.
    [GeneratedRegex(@"^[a-z]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCode();
}
