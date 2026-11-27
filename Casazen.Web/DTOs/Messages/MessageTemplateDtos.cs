using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Messages;

/// <summary>
/// A template of the host (<c>api/message-templates</c>, E05/F02/US02/T02): the <c>Body</c> with its variables (<c>{{guest_name}}</c> and the seven others),
/// the <c>Language</c> it is written in and the <c>Variables</c> it uses. An inactive one is kept and not offered by the composer.
/// </summary>
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

/// <summary>
/// The body of a template to create or change: <c>Name</c> (up to 100 characters), <c>Language</c> (one of the 14 of the listing), <c>Body</c> (up to 2000
/// characters, variables among the eight) and <c>IsActive</c>. No validation attribute: a bad value is a 422 of the service with its own code.
/// </summary>
public sealed class SaveMessageTemplateRequest
{
    public string? Name { get; set; }
    public string? Language { get; set; }
    public string? Body { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>The conversation a template is rendered for (<c>POST api/message-templates/{id}/render</c>): its key, as the hub gave it.</summary>
public sealed class RenderMessageTemplateRequest
{
    public string? ThreadKey { get; set; }
}

/// <summary>The text of a template for a conversation, with the variables filled in: plain text, to be shown as text and edited before it is sent.</summary>
public sealed class RenderedMessageTemplateDto
{
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// A template of the channel manager for a conversation (<c>GET api/messages/threads/{threadKey}/channel-templates</c>): the name and the body to copy into the
/// composer. Read only: CasaZen neither edits nor stores it.
/// </summary>
public sealed class ChannelTemplateDto
{
    public string Name { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string Text { get; init; } = string.Empty;

    public static ChannelTemplateDto From(ChannelTemplateItem item) => new() { Name = item.Name, Language = item.Language, Text = item.Text };
}

/// <summary>
/// What to translate (<c>POST api/messages/translate</c>): the conversation (<c>ThreadKey</c>), and either a message of it (<c>MessageId</c>) or a
/// <c>Text</c> the host is writing, and the language to translate into (<c>TargetLanguage</c>, one of the 14 of the listing, in any case).
/// </summary>
public sealed class TranslateMessageRequest
{
    public string? ThreadKey { get; set; }
    public Guid? MessageId { get; set; }
    public string? Text { get; set; }
    public string? TargetLanguage { get; set; }
}

/// <summary>
/// A translation, for the screen only: <c>MachineTranslated</c> is always true, <c>Provider</c> is <c>Ai</c> (CasaZen's) or <c>ChannelManager</c>, and nothing
/// of the conversation changes: the message keeps its original text.
/// </summary>
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
