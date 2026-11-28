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
    Task<EffectiveAssistantRules> GetAsync(Guid orgId, CancellationToken ct = default);

    Task<EffectiveAssistantRules> SaveAsync(Guid orgId, AssistantRulesInput rules, string userId, CancellationToken ct = default);

    Task SetEnabledAsync(Guid orgId, bool enabled, string userId, CancellationToken ct = default);

    Task<PropertyAssistantSettingsItem> GetPropertySettingsAsync(Guid orgId, Guid propertyId, CancellationToken ct = default);

    Task<PropertyAssistantSettingsItem> SavePropertySettingsAsync(
        Guid orgId,
        Guid propertyId,
        PropertyAssistantSettingsInput settings,
        string userId,
        CancellationToken ct = default);

    Task DeletePropertySettingsAsync(Guid orgId, Guid propertyId, CancellationToken ct = default);

    Task<EffectiveAssistantRules> ResolveEffectiveAsync(Guid orgId, Guid propertyId, CancellationToken ct = default);

    /// <summary>
    /// Grows <c>InstructionVersion</c> of the organization by one (an organization with no rules row gets it, with the defaults, at version 2). <b>It does not save</b>:
    /// it is called inside the unit of work of a change to the rules, the entries, the situations or the lessons, and the caller's <c>SaveChangesAsync</c> writes it with
    /// the change, in one transaction.
    /// </summary>
    Task BumpVersionAsync(Guid orgId, CancellationToken ct = default);
}

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

public sealed record PropertyAssistantSettingsInput(
    bool? Enabled,
    AssistantTone? Tone,
    string? Signature,
    TimeOnly? HoursFrom,
    TimeOnly? HoursTo,
    decimal? ConfidenceThreshold,
    IReadOnlyList<string>? ForbiddenTopics);

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
