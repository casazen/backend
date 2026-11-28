using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Assistant;

public sealed class EffectiveSituationDto
{
    public AssistantSituation Situation { get; init; }
    public string Code { get; init; } = string.Empty;
    public AssistantMode Mode { get; init; }
    public IReadOnlyList<AssistantMode> AllowedModes { get; init; } = [];
    public SensitiveCategory SensitiveCategories { get; init; }
    public bool IsSensitive { get; init; }
    public bool HasTemplate { get; init; }

    public string? Template { get; init; }

    public KnowledgeTopic? Source { get; init; }

    /// <summary>
    /// The start of the trust period (PO-08). <c>null</c> for a situation nobody configured: the start is unknown, and an unknown start is never an elapsed period, so such a situation is not
    /// eligible for the automatic modes (fail-closed); the first save of its row gives it a start.
    /// </summary>
    public DateTime? ModeSince { get; init; }
    public string Origin { get; init; } = string.Empty;

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

public sealed class SaveAssistantSituationRequest
{
    public AssistantMode? Mode { get; set; }
    public string? Template { get; set; }
    public KnowledgeTopic? Source { get; set; }
}
