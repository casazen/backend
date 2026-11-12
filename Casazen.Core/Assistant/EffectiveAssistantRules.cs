using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Assistant;

/// <summary>
/// The rules the assistant works with for an organization, or for one of its properties once the exceptions of the property are laid over them
/// (E05/F05/US01/T02, NF-66): the one value of each rule, never a «not set». What <c>IAssistantRulesService.GetAsync</c> returns for an organization and
/// <c>ResolveEffectiveAsync</c> for a property, and what the draft pipeline and the gate read.
/// </summary>
/// <param name="Enabled">The assistant works for it: the organization's switch is on and the property does not turn it off.</param>
/// <param name="Tone">How the drafts sound.</param>
/// <param name="Languages">The languages the assistant may write in: two-letter codes, at least one.</param>
/// <param name="Signature">The closing of the drafts; <c>null</c> = none.</param>
/// <param name="HoursFrom">With <paramref name="HoursTo"/>: the hours (property clock, a window may cross midnight) in which it may send by itself; both <c>null</c> = at any time.</param>
/// <param name="HoursTo">The end of the window of <paramref name="HoursFrom"/>.</param>
/// <param name="AllowedTopics">The topics and situations it may answer; <b>empty = no restriction by this list</b> (the forbidden ones are still left out).</param>
/// <param name="ForbiddenTopics">The topics and situations it never answers: a message about one of them goes to a person.</param>
/// <param name="ConfidenceThreshold">The score above which a draft may be sent by itself (PO-08: 0.85 to start).</param>
/// <param name="InstructionVersion">The version of the instructions: it grows with every change that affects drafting, and the decision log keeps the one a draft was written with.</param>
public sealed record EffectiveAssistantRules(
    bool Enabled,
    AssistantTone Tone,
    IReadOnlyList<string> Languages,
    string? Signature,
    TimeOnly? HoursFrom,
    TimeOnly? HoursTo,
    IReadOnlyList<string> AllowedTopics,
    IReadOnlyList<string> ForbiddenTopics,
    decimal ConfidenceThreshold,
    int InstructionVersion)
{
    /// <summary>
    /// What an organization that never saved its rules has: the assistant off, a neutral tone, Italian and English, no signature, any hour, no list of topics, a
    /// threshold of 0.85 and the first version of the instructions.
    /// </summary>
    public static EffectiveAssistantRules Defaults { get; } = new(
        Enabled: false,
        Tone: AssistantTone.Neutral,
        Languages: OrgAssistantRules.DefaultLanguages,
        Signature: null,
        HoursFrom: null,
        HoursTo: null,
        AllowedTopics: [],
        ForbiddenTopics: [],
        ConfidenceThreshold: OrgAssistantRules.DefaultConfidenceThreshold,
        InstructionVersion: 1);

    /// <summary>The rules an organization saved (or the defaults for the part it never set), as the row says them.</summary>
    public static EffectiveAssistantRules Of(OrgAssistantRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return new EffectiveAssistantRules(
            rules.Enabled,
            rules.Tone,
            [.. rules.Languages],
            rules.Signature,
            rules.HoursFrom,
            rules.HoursTo,
            [.. rules.AllowedTopics],
            [.. rules.ForbiddenTopics],
            rules.ConfidenceThreshold,
            rules.InstructionVersion);
    }

    /// <summary>
    /// The rules of a property: its own value of each field, else the organization's. <c>Enabled</c> is on only when the organization's switch is on <b>and</b> the
    /// property does not turn it off (it can only switch it off); the hours are the property's when it gives both ends; a list of forbidden topics of the
    /// property replaces the organization's, and a property that gives none keeps the organization's.
    /// </summary>
    public EffectiveAssistantRules WithExceptionsOf(PropertyAssistantSettings? property)
    {
        if (property is null)
            return this;

        var ownHours = property.HoursFrom is not null && property.HoursTo is not null;
        return this with
        {
            Enabled = Enabled && (property.Enabled ?? true),
            Tone = property.Tone ?? Tone,
            Signature = property.Signature ?? Signature,
            HoursFrom = ownHours ? property.HoursFrom : HoursFrom,
            HoursTo = ownHours ? property.HoursTo : HoursTo,
            ForbiddenTopics = property.ForbiddenTopics is { } forbidden ? [.. forbidden] : ForbiddenTopics,
            ConfidenceThreshold = property.ConfidenceThreshold ?? ConfidenceThreshold,
        };
    }
}
