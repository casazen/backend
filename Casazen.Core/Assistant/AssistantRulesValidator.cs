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
/// empty list is 422, never «every language»). The lists of topics are optional filters that have to name topics the assistant knows: no allowed list, and an empty one in a request, is the
/// state of an organization that never saved its rules (no restriction by it: <b>the one place where an empty list means «all»</b>, declared, and the gate of the later tasks must read it so),
/// an empty forbidden list forbids nothing, and neither lets a sensitive situation go automatic (the rule of the modes, <see cref="AssistantModeRules"/>, does not read them).
/// </remarks>
public static partial class AssistantRulesValidator
{
    public const string RulesInvalidCode = "assistant_rules_invalid";

    public const string ThresholdInvalidCode = "assistant_threshold_invalid";

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

    public static decimal CleanThreshold(decimal? threshold) => threshold is null ? OrgAssistantRules.DefaultConfidenceThreshold : Checked(threshold.Value);

    public static decimal? CleanOptionalThreshold(decimal? threshold) => threshold is null ? null : Checked(threshold.Value);

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

    [GeneratedRegex(@"^[a-z]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCode();
}
