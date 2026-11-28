using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Assistant;

public sealed class ListingFactDto
{
    public KnowledgeTopic Topic { get; init; }
    public string Text { get; init; } = string.Empty;
    public string SourceField { get; init; } = string.Empty;

    public static ListingFactDto From(ListingFact fact) => new() { Topic = fact.Topic, Text = fact.Text, SourceField = fact.SourceField };
}

public sealed class KnowledgeEntryDto
{
    public Guid Id { get; init; }
    public Guid? PropertyId { get; init; }
    public KnowledgeTopic Topic { get; init; }
    public KnowledgeEntryKind Kind { get; init; }
    public KnowledgeSource Source { get; init; }
    public string? Title { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Language { get; init; }
    public Guid? LessonId { get; init; }
    public bool IsActive { get; init; }
    public bool Inherited { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static KnowledgeEntryDto From(KnowledgeEntryItem item) => new()
    {
        Id = item.Id,
        PropertyId = item.PropertyId,
        Topic = item.Topic,
        Kind = item.Kind,
        Source = item.Source,
        Title = item.Title,
        Text = item.Text,
        Language = item.Language,
        LessonId = item.LessonId,
        IsActive = item.IsActive,
        Inherited = item.Inherited,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };
}

public sealed class AssistantKnowledgeViewDto
{
    public Guid PropertyId { get; init; }
    public string Language { get; init; } = string.Empty;
    public IReadOnlyList<ListingFactDto> Facts { get; init; } = [];
    public IReadOnlyList<KnowledgeEntryDto> Entries { get; init; } = [];
    public IReadOnlyList<KnowledgeTopic> TopicsWithoutData { get; init; } = [];

    public static AssistantKnowledgeViewDto From(AssistantKnowledgeView view) => new()
    {
        PropertyId = view.PropertyId,
        Language = view.Language,
        Facts = [.. view.Facts.Select(ListingFactDto.From)],
        Entries = [.. view.Entries.Select(KnowledgeEntryDto.From)],
        TopicsWithoutData = view.TopicsWithoutData,
    };
}

public sealed class SaveKnowledgeEntryRequest
{
    public KnowledgeTopic? Topic { get; set; }
    public KnowledgeEntryKind? Kind { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Language { get; set; }
    public bool? IsActive { get; set; }

    public KnowledgeEntryInput ToInput() => new(Topic, Kind, Title, Text, Language, IsActive);
}

public sealed class AssistantRulesDto
{
    public bool Enabled { get; init; }
    public AssistantTone Tone { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public string? Signature { get; init; }
    public TimeOnly? HoursFrom { get; init; }
    public TimeOnly? HoursTo { get; init; }
    public IReadOnlyList<string> AllowedTopics { get; init; } = [];
    public IReadOnlyList<string> ForbiddenTopics { get; init; } = [];
    public decimal ConfidenceThreshold { get; init; }
    public int InstructionVersion { get; init; }

    public static AssistantRulesDto From(EffectiveAssistantRules rules) => new()
    {
        Enabled = rules.Enabled,
        Tone = rules.Tone,
        Languages = rules.Languages,
        Signature = rules.Signature,
        HoursFrom = rules.HoursFrom,
        HoursTo = rules.HoursTo,
        AllowedTopics = rules.AllowedTopics,
        ForbiddenTopics = rules.ForbiddenTopics,
        ConfidenceThreshold = rules.ConfidenceThreshold,
        InstructionVersion = rules.InstructionVersion,
    };
}

public sealed class SaveAssistantRulesRequest
{
    public bool? Enabled { get; set; }
    public AssistantTone? Tone { get; set; }
    public List<string>? Languages { get; set; }
    public string? Signature { get; set; }
    public TimeOnly? HoursFrom { get; set; }
    public TimeOnly? HoursTo { get; set; }
    public List<string>? AllowedTopics { get; set; }
    public List<string>? ForbiddenTopics { get; set; }
    public decimal? ConfidenceThreshold { get; set; }

    public AssistantRulesInput ToInput() =>
        new(Enabled, Tone, Languages, Signature, HoursFrom, HoursTo, AllowedTopics, ForbiddenTopics, ConfidenceThreshold);
}

public sealed class PropertyAssistantSettingsDto
{
    public Guid PropertyId { get; init; }
    public bool? Enabled { get; init; }
    public AssistantTone? Tone { get; init; }
    public string? Signature { get; init; }
    public TimeOnly? HoursFrom { get; init; }
    public TimeOnly? HoursTo { get; init; }
    public decimal? ConfidenceThreshold { get; init; }
    public IReadOnlyList<string>? ForbiddenTopics { get; init; }
    public DateTime? UpdatedAt { get; init; }

    public static PropertyAssistantSettingsDto From(PropertyAssistantSettingsItem item) => new()
    {
        PropertyId = item.PropertyId,
        Enabled = item.Enabled,
        Tone = item.Tone,
        Signature = item.Signature,
        HoursFrom = item.HoursFrom,
        HoursTo = item.HoursTo,
        ConfidenceThreshold = item.ConfidenceThreshold,
        ForbiddenTopics = item.ForbiddenTopics,
        UpdatedAt = item.UpdatedAt,
    };
}

/// <summary>
/// The body of <c>PUT api/assistant/properties/{propertyId}/settings</c>: the whole set of the exceptions; a field not given is «as the organization». <c>enabled</c> can only switch the
/// assistant off for the property (the organization's switch must be on as well). A list of <c>forbiddenTopics</c> replaces the organization's (an empty one means nothing is forbidden for
/// this property). Giving no field at all removes the exceptions.
/// </summary>
public sealed class SavePropertyAssistantSettingsRequest
{
    public bool? Enabled { get; set; }
    public AssistantTone? Tone { get; set; }
    public string? Signature { get; set; }
    public TimeOnly? HoursFrom { get; set; }
    public TimeOnly? HoursTo { get; set; }
    public decimal? ConfidenceThreshold { get; set; }
    public List<string>? ForbiddenTopics { get; set; }

    public PropertyAssistantSettingsInput ToInput() =>
        new(Enabled, Tone, Signature, HoursFrom, HoursTo, ConfidenceThreshold, ForbiddenTopics);
}
