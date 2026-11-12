using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// How an organization wants its assistant to write, and what a property does differently (E05/F05/US01/T02, NF-66): tone, languages, signature, hours,
/// allowed and forbidden topics and the confidence threshold, with the organization's switch and the exceptions of each property (inheritance organization to
/// property). The organization's rules are one row (<c>OrgAssistantRules</c>), a property's exceptions another (<c>PropertyAssistantSettings</c>, a null field means
/// «as the organization»); an organization that never saved anything has the defaults, which are returned without writing.
/// </summary>
/// <remarks>
/// <para><b>Tenant.</b> Every method names the organization and every query filters by it, on top of the tenant filter of the context: another organization's rules,
/// exceptions and properties are never read or written. A property that is not the organization's (or is deleted) is 404 <c>property_not_found</c>. The services
/// never check roles: the web layer decides who may call (org administrators for the organization's rules, <c>property.write</c> on the property for its exceptions).</para>
/// <para><b>The version of the instructions.</b> <see cref="BumpVersionAsync"/> grows <c>InstructionVersion</c> by one, so the decision log can say which instructions a
/// draft was written with. Every change to the rules, the exceptions, the knowledge entries, the situations and the lessons calls it <b>in the unit of work of the
/// change</b>: it only tracks the new version, and the caller's one <c>SaveChangesAsync</c> writes it with the change itself. The version is the concurrency token of
/// the row: two changes at the same moment cannot both claim it, the second one is 409 <c>assistant_concurrent_change</c> and nothing of it is written.</para>
/// </remarks>
public interface IAssistantRulesService
{
    /// <summary>The rules of the organization; the defaults (<see cref="EffectiveAssistantRules.Defaults"/>) when it never saved any, and nothing is written for that.</summary>
    Task<EffectiveAssistantRules> GetAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Saves the rules of the organization (the whole set: what the request does not give is the default). 422 <c>assistant_rules_invalid</c> (no language, a code
    /// that is not two lower-case letters, a signature over 300 characters, hours with one end only or with the start equal to the end, a topic the assistant does
    /// not know) or <c>assistant_threshold_invalid</c> (a threshold outside 0.50 to 0.99). The version of the instructions grows when something changed.
    /// </summary>
    Task<EffectiveAssistantRules> SaveAsync(Guid orgId, AssistantRulesInput rules, string userId, CancellationToken ct = default);

    /// <summary>
    /// The exceptions of a property, a null field meaning «as the organization»; all null when the property has none. 404 <c>property_not_found</c> for a property that
    /// is not the organization's.
    /// </summary>
    Task<PropertyAssistantSettingsItem> GetPropertySettingsAsync(Guid orgId, Guid propertyId, CancellationToken ct = default);

    /// <summary>
    /// Saves the exceptions of a property (the whole set: a field not given is «as the organization»). The same 422 as <see cref="SaveAsync"/> for the fields it has,
    /// 404 <c>property_not_found</c>. The version of the instructions grows when something changed.
    /// </summary>
    Task<PropertyAssistantSettingsItem> SavePropertySettingsAsync(
        Guid orgId,
        Guid propertyId,
        PropertyAssistantSettingsInput settings,
        string userId,
        CancellationToken ct = default);

    /// <summary>Removes the exceptions of a property: it follows the organization again. Nothing to remove is not an error; 404 <c>property_not_found</c> for a property that is not the organization's.</summary>
    Task DeletePropertySettingsAsync(Guid orgId, Guid propertyId, CancellationToken ct = default);

    /// <summary>
    /// The rules in force for a property: its own value of each field, else the organization's. <see cref="EffectiveAssistantRules.Enabled"/> is true only when the
    /// organization's switch is on and the property does not turn it off. Reads only, whoever calls (the draft pipeline and the gate included).
    /// </summary>
    Task<EffectiveAssistantRules> ResolveEffectiveAsync(Guid orgId, Guid propertyId, CancellationToken ct = default);

    /// <summary>
    /// Grows <c>InstructionVersion</c> of the organization by one (an organization with no rules row gets it, with the defaults, at version 2). <b>It does not save</b>:
    /// it is called inside the unit of work of a change to the rules, the entries, the situations or the lessons, and the caller's <c>SaveChangesAsync</c> writes it with
    /// the change, in one transaction.
    /// </summary>
    Task BumpVersionAsync(Guid orgId, CancellationToken ct = default);
}

/// <summary>
/// The rules of an organization as a request gives them (the whole set). <paramref name="Languages"/> not given are Italian and English, <paramref name="Tone"/> not
/// given is neutral, <paramref name="ConfidenceThreshold"/> not given is 0.85, the lists not given are empty, <paramref name="Enabled"/> not given is off.
/// </summary>
public sealed record AssistantRulesInput(
    bool? Enabled,
    AssistantTone? Tone,
    IReadOnlyList<string>? Languages,
    string? Signature,
    TimeOnly? HoursFrom,
    TimeOnly? HoursTo,
    IReadOnlyList<string>? AllowedTopics,
    IReadOnlyList<string>? ForbiddenTopics,
    decimal? ConfidenceThreshold);

/// <summary>The exceptions of a property as a request gives them (the whole set): a field not given is «as the organization».</summary>
public sealed record PropertyAssistantSettingsInput(
    bool? Enabled,
    AssistantTone? Tone,
    string? Signature,
    TimeOnly? HoursFrom,
    TimeOnly? HoursTo,
    decimal? ConfidenceThreshold,
    IReadOnlyList<string>? ForbiddenTopics);

/// <summary>The exceptions of a property as the screens read them: every field null means the property has none and follows the organization.</summary>
public sealed record PropertyAssistantSettingsItem(
    Guid PropertyId,
    bool? Enabled,
    AssistantTone? Tone,
    string? Signature,
    TimeOnly? HoursFrom,
    TimeOnly? HoursTo,
    decimal? ConfidenceThreshold,
    IReadOnlyList<string>? ForbiddenTopics,
    DateTime? UpdatedAt);
