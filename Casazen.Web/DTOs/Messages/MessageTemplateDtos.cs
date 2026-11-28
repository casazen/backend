using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Messages;

public sealed class MessageTemplateDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public IReadOnlyList<string> Variables { get; init; } = [];
    public DateTime UpdatedAt { get; init; }

    public static MessageTemplateDto From(MessageTemplateItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Language = item.Language,
        Body = item.Body,
        IsActive = item.IsActive,
        Variables = item.Variables,
        UpdatedAt = item.UpdatedAt,
    };
}

public sealed class SaveMessageTemplateRequest
{
    public string? Name { get; set; }
    public string? Language { get; set; }
    public string? Body { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class RenderMessageTemplateRequest
{
    public string? ThreadKey { get; set; }
}

public sealed class RenderedMessageTemplateDto
{
    public string Text { get; init; } = string.Empty;
}

public sealed class ChannelTemplateDto
{
    public string Name { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string Text { get; init; } = string.Empty;

    public static ChannelTemplateDto From(ChannelTemplateItem item) => new() { Name = item.Name, Language = item.Language, Text = item.Text };
}

public sealed class TranslateMessageRequest
{
    public string? ThreadKey { get; set; }
    public Guid? MessageId { get; set; }
    public string? Text { get; set; }
    public string? TargetLanguage { get; set; }
}

public sealed class MessageTranslationDto
{
    public string Text { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public MessageTranslationProvider Provider { get; init; }
    public bool MachineTranslated { get; init; }

    public static MessageTranslationDto From(MessageTranslation translation) => new()
    {
        Text = translation.Text,
        TargetLanguage = translation.TargetLanguage,
        Provider = translation.Provider,
        MachineTranslated = translation.MachineTranslated,
    };
}
