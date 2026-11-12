using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// What the assistant does when it recognizes a situation (E05/F05/US01/T03, NF-66, NF-67): the mode (escalate, draft, automatic, automatic with exceptions), the text of the answer
/// when there is a template, the topic it comes from when there is none, and the start of the trust period (PO-08), for the organization and, as exceptions, for each property. The
/// situations are those of <see cref="AssistantSituationCatalog"/>; a situation nobody configured has the default of the catalog (draft, an emergency in escalate).
/// </summary>
/// <remarks>
/// <para><b>Inheritance.</b> The value of a property is its own row, else the organization's row, else the catalog's default: <b>a property row is a whole row</b> (mode, template and
/// source), it replaces the organization's and does not merge with it. Removing the property's row brings the organization's value back.</para>
/// <para><b>The rule of the modes.</b> A sensitive situation, and <c>Other</c>, can be saved only as escalate or draft: 422 <c>assistant_mode_not_allowed</c>, whoever asks
/// (<see cref="AssistantModeRules"/>). What is read is clamped by the same rule, so a row that breaks it (stored by hand) is read as a draft.</para>
/// <para><b>Tenant.</b> Every method names the organization and every query filters by it, on top of the tenant filter of the context; a property that is not the organization's is 404
/// <c>property_not_found</c>. The services never check roles: the web layer decides who may call, and who may read the templates.</para>
/// <para><b>One unit of work.</b> A change that really changes something grows the version of the instructions
/// (<see cref="IAssistantRulesService.BumpVersionAsync"/>) in the same save; a save of the same values changes nothing. The template is the host's own text and is encrypted at rest;
/// it is never logged.</para>
/// </remarks>
public interface IAssistantSituationService
{
    /// <summary>
    /// The 15 situations with their effective value for the organization (<paramref name="propertyId"/> null) or for a property: the property's row over the organization's over the
    /// default. The template is returned in clear (the web layer shows it only to the caller who may edit). 404 <c>property_not_found</c>.
    /// </summary>
    Task<IReadOnlyList<EffectiveSituation>> ListEffectiveAsync(Guid orgId, Guid? propertyId, CancellationToken ct = default);

    /// <summary>
    /// Saves a situation for the organization (<paramref name="propertyId"/> null) or as the exception of a property: the mode (422 <c>assistant_mode_not_allowed</c> when the situation
    /// does not allow it), the text of the answer, with variables that <c>MessageTemplateRenderer</c> knows (422 <c>assistant_template_invalid</c> naming the variable it does not,
    /// or for a text that is too long), and the topic the answer comes from when there is no template. <c>ModeSince</c> becomes now when the row is new or the mode changed, and is
    /// not moved by a change of the template. Returns the value as it stands for that scope.
    /// </summary>
    Task<EffectiveSituation> SaveAsync(
        Guid orgId,
        Guid? propertyId,
        AssistantSituation situation,
        AssistantMode mode,
        string? template,
        KnowledgeTopic? source,
        string userId,
        CancellationToken ct = default);

    /// <summary>Removes the exception of a property for a situation: the property goes back to the organization's value. Nothing to remove is not an error. 404 <c>property_not_found</c>.</summary>
    Task DeleteExceptionAsync(Guid orgId, Guid propertyId, AssistantSituation situation, CancellationToken ct = default);

    /// <summary>
    /// <b>For the draft pipeline and the gate.</b> The value of one situation for a property (the same merge as <see cref="ListEffectiveAsync"/>): the mode (clamped by the rule of the
    /// modes), the template in clear, the source topic (the situation's own when the row gives none) and the start of the trust period (<c>null</c> for a situation nobody configured).
    /// Reads only; the template goes to the pipeline and nowhere else.
    /// </summary>
    Task<ResolvedSituation> ResolveAsync(Guid orgId, Guid? propertyId, AssistantSituation situation, CancellationToken ct = default);
}

/// <summary>Where the value of a situation comes from: the catalog's default, the organization's row, or the exception of the property.</summary>
public enum AssistantSettingOrigin
{
    Default = 0,
    Organization = 1,
    Property = 2,
}

/// <summary>A situation with its effective value, as the screens read it.</summary>
/// <param name="Situation">The situation.</param>
/// <param name="Code">Its stable code, the key of the label the clients show.</param>
/// <param name="Mode">What the assistant does when it recognizes it (already clamped by the rule of the modes).</param>
/// <param name="HasTemplate">There is a text for the answer; the text itself is <paramref name="Template"/>, which the web layer gives only to the caller who may edit.</param>
/// <param name="Template">The text of the answer, with <c>{{variables}}</c>, in clear; <c>null</c> when there is none.</param>
/// <param name="Source">The topic of the knowledge base the answer comes from when there is no template.</param>
/// <param name="ModeSince">The start of the current trust period; <c>null</c> for a situation nobody configured.</param>
/// <param name="Origin">Where the value comes from.</param>
/// <param name="SensitiveCategories">The categories the situation carries by nature.</param>
/// <param name="AllowedModes">The modes the situation may be set to.</param>
public sealed record EffectiveSituation(
    AssistantSituation Situation,
    string Code,
    AssistantMode Mode,
    bool HasTemplate,
    string? Template,
    KnowledgeTopic? Source,
    DateTime? ModeSince,
    AssistantSettingOrigin Origin,
    SensitiveCategory SensitiveCategories,
    IReadOnlyList<AssistantMode> AllowedModes);

/// <summary>What the pipeline and the gate read for one situation of one property.</summary>
/// <param name="Mode">What the assistant does (already clamped by the rule of the modes).</param>
/// <param name="Template">The text of the answer, in clear; <c>null</c> when the answer is written from the <paramref name="Source"/>.</param>
/// <param name="Source">The topic of the knowledge base the answer comes from.</param>
/// <param name="ModeSince">The start of the current trust period; <c>null</c> for a situation nobody configured.</param>
public sealed record ResolvedSituation(AssistantMode Mode, string? Template, KnowledgeTopic? Source, DateTime? ModeSince);
