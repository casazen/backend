using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Compliance;

/// <summary>
/// One item of the cockpit as the assistant sees it («Gestisci con AI»): what it is about, who does it and why, in stable codes the web app
/// translates (<see cref="AutopilotReasons"/>). The enums are serialized by name.
/// </summary>
public class AutopilotItemDto
{
    /// <summary><c>{topic}:{bookingId}</c>: what <see cref="AutopilotRunRequest.Keys"/> sends back to run exactly what was shown.</summary>
    public string Key { get; set; } = string.Empty;

    public AutopilotTopic Topic { get; set; }

    /// <summary><c>Ready</c> (the assistant does it in the next run), <c>Waiting</c> or <c>Host</c> (only the host can).</summary>
    public AutopilotItemStatus Status { get; set; }

    /// <summary>Why: <c>ask_guests</c>, <c>asked_recently</c>, <c>no_contact</c>, <c>close_departure</c>, <c>awaiting_guest_data</c>, <c>ready_to_upload</c>, ...</summary>
    public string Reason { get; set; } = string.Empty;

    public Guid BookingId { get; set; }
    public Guid PropertyId { get; set; }

    /// <summary>The name of the cockpit item (the booker).</summary>
    public string GuestName { get; set; } = string.Empty;

    public string PropertyName { get; set; } = string.Empty;

    /// <summary>For a <c>GuestData</c> item that is <c>Ready</c>: <c>email</c> and/or <c>chat</c>, how the guest can be reached.</summary>
    public IEnumerable<string> Channels { get; set; } = [];

    /// <summary>When it is due (UTC), as the cockpit says; null when no date fixes it.</summary>
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

/// <summary>What the assistant would do now (<c>GET api/compliance/autopilot</c>): nothing is changed and nobody is contacted.</summary>
public class AutopilotPlanDto
{
    public IEnumerable<AutopilotItemDto> Items { get; set; } = [];

    /// <summary>An AI provider is in effect: the chat messages are written by it (otherwise they are plain texts of CasaZen).</summary>
    public bool AiAvailable { get; set; }

    /// <summary>The <c>Ready</c> items one run does not reach.</summary>
    public int BeyondLimit { get; set; }

    public static AutopilotPlanDto From(AutopilotPlan plan) => new()
    {
        Items = plan.Items.Select(AutopilotItemDto.From).ToList(),
        AiAvailable = plan.AiAvailable,
        BeyondLimit = plan.BeyondLimit,
    };
}

/// <summary>What to run (<c>POST api/compliance/autopilot/run</c>).</summary>
public class AutopilotRunRequest
{
    /// <summary>
    /// The keys of the items the host was shown (<see cref="AutopilotItemDto.Key"/>): only those still <c>Ready</c> are done, so what runs
    /// is what was shown and never something that appeared since. Null or empty = every item that is <c>Ready</c> now. At most 200 are read.
    /// </summary>
    public List<string>? Keys { get; set; }
}

/// <summary>
/// A message for a guest that the host sends from his own phone: the text with the link, and the links that open WhatsApp and the
/// messages app with it written (null when the guest has no number that can be written in international form).
/// </summary>
public class AutopilotChatDraftDto
{
    /// <summary>The language of the text: <c>it</c>, <c>en</c>, <c>de</c>, <c>fr</c> or <c>es</c>.</summary>
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

/// <summary>How an item ended: stable codes (<see cref="AutopilotOutcomeCodes"/>) the web app translates.</summary>
public class AutopilotOutcomeDto
{
    public string Key { get; set; } = string.Empty;
    public AutopilotTopic Topic { get; set; }
    public Guid BookingId { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;

    /// <summary><c>Done</c>, <c>Failed</c> or <c>Skipped</c> (it was no longer to be done).</summary>
    public AutopilotOutcomeStatus Status { get; set; }

    /// <summary><c>email_queued</c>, <c>chat_ready</c>, <c>email_failed</c>, <c>departure_closed</c>, <c>failed</c>, <c>no_longer_needed</c>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary><c>Queued</c> or <c>Failed</c> for the e-mail with the link; null when none was asked for.</summary>
    public string? EmailStatus { get; set; }

    /// <summary>Why the e-mail did not leave: <c>no_recipient</c>, <c>provider_not_configured</c>, <c>queue_failed</c>, ...</summary>
    public string? EmailError { get; set; }

    /// <summary>The message the host can send by WhatsApp or SMS; null when the guest has no number or the item was not about the data of the guests.</summary>
    public AutopilotChatDraftDto? Chat { get; set; }

    /// <summary>The code of the rule that refused, for a <c>Failed</c> item (<c>booking_already_checked_out</c>, ...).</summary>
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

/// <summary>What the run did (<c>POST api/compliance/autopilot/run</c>).</summary>
public class AutopilotRunResponse
{
    public IEnumerable<AutopilotOutcomeDto> Outcomes { get; set; } = [];

    /// <summary>The wording of at least one chat message is the AI's.</summary>
    public bool AiUsed { get; set; }

    /// <summary>Why the AI did not write the messages when it was asked: <c>ai_not_configured</c>, <c>ai_budget_exhausted</c>, <c>ai_unavailable</c>, <c>ai_invalid_answer</c>.</summary>
    public string? AiReason { get; set; }

    /// <summary>What this run did not reach (the limits of one run): run again.</summary>
    public int BeyondLimit { get; set; }

    public static AutopilotRunResponse From(AutopilotRunResult result) => new()
    {
        Outcomes = result.Outcomes.Select(AutopilotOutcomeDto.From).ToList(),
        AiUsed = result.AiUsed,
        AiReason = result.AiReason,
        BeyondLimit = result.BeyondLimit,
    };
}
