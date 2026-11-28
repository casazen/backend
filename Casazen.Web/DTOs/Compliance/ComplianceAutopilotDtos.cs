using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Compliance;

public class AutopilotItemDto
{
    public string Key { get; set; } = string.Empty;

    public AutopilotTopic Topic { get; set; }

    public AutopilotItemStatus Status { get; set; }

    public string Reason { get; set; } = string.Empty;

    public Guid BookingId { get; set; }
    public Guid PropertyId { get; set; }

    public string GuestName { get; set; } = string.Empty;

    public string PropertyName { get; set; } = string.Empty;

    public IEnumerable<string> Channels { get; set; } = [];

    public DateTime? DueAt { get; set; }

    public static AutopilotItemDto From(AutopilotItem item) => new()
    {
        Key = item.Key,
        Topic = item.Topic,
        Status = item.Status,
        Reason = item.Reason,
        BookingId = item.BookingId,
        PropertyId = item.PropertyId,
        GuestName = item.GuestName,
        PropertyName = item.PropertyName,
        Channels = item.Channels,
        DueAt = item.DueAt,
    };
}

public class AutopilotPlanDto
{
    public IEnumerable<AutopilotItemDto> Items { get; set; } = [];

    /// <summary>An AI provider is in effect: the chat messages are written by it (otherwise they are plain texts of CasaZen).</summary>
    public bool AiAvailable { get; set; }

    public int BeyondLimit { get; set; }

    public static AutopilotPlanDto From(AutopilotPlan plan) => new()
    {
        Items = plan.Items.Select(AutopilotItemDto.From).ToList(),
        AiAvailable = plan.AiAvailable,
        BeyondLimit = plan.BeyondLimit,
    };
}

public class AutopilotRunRequest
{
    /// <summary>
    /// The keys of the items the host was shown (<see cref="AutopilotItemDto.Key"/>): only those still <c>Ready</c> are done, so what runs
    /// is what was shown and never something that appeared since. Null or empty = every item that is <c>Ready</c> now. At most 200 are read.
    /// </summary>
    public List<string>? Keys { get; set; }
}

public class AutopilotChatDraftDto
{
    public string Language { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
    public string? WhatsAppUrl { get; set; }
    public string? SmsUrl { get; set; }

    /// <summary>The wording is the AI's (otherwise it is a plain text of CasaZen).</summary>
    public bool AiWritten { get; set; }

    public static AutopilotChatDraftDto From(AutopilotChatDraft draft) => new()
    {
        Language = draft.Language,
        Text = draft.Text,
        WhatsAppUrl = draft.WhatsAppUrl,
        SmsUrl = draft.SmsUrl,
        AiWritten = draft.AiWritten,
    };
}

public class AutopilotOutcomeDto
{
    public string Key { get; set; } = string.Empty;
    public AutopilotTopic Topic { get; set; }
    public Guid BookingId { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;

    public AutopilotOutcomeStatus Status { get; set; }

    public string Code { get; set; } = string.Empty;

    public string? EmailStatus { get; set; }

    public string? EmailError { get; set; }

    public AutopilotChatDraftDto? Chat { get; set; }

    public string? ErrorCode { get; set; }

    public static AutopilotOutcomeDto From(AutopilotOutcome outcome) => new()
    {
        Key = outcome.Key,
        Topic = outcome.Topic,
        BookingId = outcome.BookingId,
        GuestName = outcome.GuestName,
        PropertyName = outcome.PropertyName,
        Status = outcome.Status,
        Code = outcome.Code,
        EmailStatus = outcome.EmailStatus,
        EmailError = outcome.EmailError,
        Chat = outcome.Chat is null ? null : AutopilotChatDraftDto.From(outcome.Chat),
        ErrorCode = outcome.ErrorCode,
    };
}

public class AutopilotRunResponse
{
    public IEnumerable<AutopilotOutcomeDto> Outcomes { get; set; } = [];

    public bool AiUsed { get; set; }

    public string? AiReason { get; set; }

    public int BeyondLimit { get; set; }

    public static AutopilotRunResponse From(AutopilotRunResult result) => new()
    {
        Outcomes = result.Outcomes.Select(AutopilotOutcomeDto.From).ToList(),
        AiUsed = result.AiUsed,
        AiReason = result.AiReason,
        BeyondLimit = result.BeyondLimit,
    };
}
