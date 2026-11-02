namespace Casazen.Core.Services;

/// <summary>
/// Writes the chat messages (WhatsApp, SMS) with which the assistant asks the guests of a stay for their data: one text per language and
/// per reminder, with the placeholders of <see cref="AutopilotChatText"/>. It never sees a guest: the request carries a language and the
/// number of the reminder, nothing else, and what comes back is checked before it is used.
/// </summary>
public interface IAutopilotMessageWriter
{
    /// <summary>
    /// A text for every variant: the AI's when an AI is in effect, it answered and the answer passed <see cref="AutopilotChatText.Check"/>;
    /// otherwise the plain text of CasaZen. Never an error for the caller: the result says why the AI was not used.
    /// </summary>
    /// <param name="orgId">The org the run belongs to: the scope of the cache of the answers.</param>
    /// <param name="variants">What is needed: a language and a reminder each.</param>
    /// <param name="acquirePermit">Takes a permit of the AI rate limit right before the call; false = used up. Null = no limit.</param>
    Task<AutopilotWording> WriteAsync(
        Guid orgId,
        IReadOnlyCollection<AutopilotChatVariant> variants,
        Func<bool>? acquirePermit,
        CancellationToken cancellationToken = default);
}

/// <summary>A text to write: the language of the guest and which message it is (1 first, 2 and 3 reminders).</summary>
public sealed record AutopilotChatVariant(string Language, int Reminder)
{
    public string Key => AutopilotChatText.VariantKey(Language, Reminder);
}

/// <summary>A template with the placeholders of <see cref="AutopilotChatText"/>, and who wrote it.</summary>
public sealed record AutopilotWrittenText(string Template, bool AiWritten);

/// <param name="Texts">One text per <see cref="AutopilotChatVariant.Key"/> asked.</param>
/// <param name="AiUsed">At least one text is the AI's.</param>
/// <param name="AiReason">Why the AI was asked for and not used (<see cref="AiSuggestionFallbackReasons"/>); null when it was used or not asked.</param>
public sealed record AutopilotWording(IReadOnlyDictionary<string, AutopilotWrittenText> Texts, bool AiUsed, string? AiReason);
