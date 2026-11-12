using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Assistant;

/// <summary>
/// A situation with its effective value (<c>api/assistant/situations</c>, <c>api/assistant/properties/{propertyId}/situations</c>, E05/F05/US01/T03): the stable <c>code</c> (the key of the
/// label the client shows), the <c>mode</c> (<c>Escalate</c>, <c>Draft</c>, <c>Automatic</c>, <c>AutomaticWithExceptions</c>), the <c>allowedModes</c> (only escalate and draft for a
/// sensitive situation), whether there is a <c>template</c> for the answer (the text itself only for the caller who may edit it) or a <c>source</c> topic of the knowledge base, <c>modeSince</c> (the
/// start of the trust period, <c>null</c> for a situation nobody configured) and where the value comes from (<c>origin</c>: <c>default</c>, <c>organization</c>, <c>property</c>).
/// </summary>
public sealed class EffectiveSituationDto
{
    public AssistantSituation Situation { get; init; }
    public string Code { get; init; } = string.Empty;
    public AssistantMode Mode { get; init; }
    public IReadOnlyList<AssistantMode> AllowedModes { get; init; } = [];
    public SensitiveCategory SensitiveCategories { get; init; }
    public bool IsSensitive { get; init; }
    public bool HasTemplate { get; init; }

    /// <summary>The text of the answer: only for a caller who may edit it, <c>null</c> for everybody else (use <see cref="HasTemplate"/>).</summary>
    public string? Template { get; init; }

    public KnowledgeTopic? Source { get; init; }
    public DateTime? ModeSince { get; init; }
    public string Origin { get; init; } = string.Empty;

    /// <summary>The situation as the screens read it; <paramref name="includeTemplate"/> says whether the caller may see the text of the template.</summary>
    public static EffectiveSituationDto From(EffectiveSituation item, bool includeTemplate) => new()
    {
        Situation = item.Situation,
        Code = item.Code,
        Mode = item.Mode,
        AllowedModes = item.AllowedModes,
        SensitiveCategories = item.SensitiveCategories,
        IsSensitive = item.SensitiveCategories != SensitiveCategory.None,
        HasTemplate = item.HasTemplate,
        Template = includeTemplate ? item.Template : null,
        Source = item.Source,
        ModeSince = item.ModeSince,
        Origin = item.Origin switch
        {
            AssistantSettingOrigin.Property => "property",
            AssistantSettingOrigin.Organization => "organization",
            _ => "default",
        },
    };
}

/// <summary>
/// The body of <c>PUT api/assistant/situations/{situation}</c> and <c>PUT api/assistant/properties/{propertyId}/situations/{situation}</c>: the <c>mode</c> (the default of the situation
/// when not given; 422 <c>assistant_mode_not_allowed</c> for an automatic mode on a sensitive situation), the <c>template</c> (up to 1,000 characters, with the eight variables of the
/// message templates; 422 <c>assistant_template_invalid</c> naming the variable it does not know) and the <c>source</c> topic of the knowledge base the answer comes from when there is no
/// template. The whole row: what the body does not give is none. No validation attribute: a bad value is a 422 of the service.
/// </summary>
public sealed class SaveAssistantSituationRequest
{
    public AssistantMode? Mode { get; set; }
    public string? Template { get; set; }
    public KnowledgeTopic? Source { get; set; }
}
