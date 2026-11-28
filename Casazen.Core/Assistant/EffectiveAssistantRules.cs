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
/// <param name="AllowedTopics">
/// The topics and situations it may answer; <b>empty or missing = no restriction by this list</b> (the forbidden ones are still left out). Declared: it is the one place where an empty list in a
/// request means «all» (the state of an organization that never saved its rules, which <c>GET</c> gives back); the gate of the later tasks must read it so, and «nothing allowed» would take an explicit
/// flag, not an empty list.
/// </param>
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
