namespace Casazen.Core.Services;

/// <summary>
/// Why a text the AI should have suggested is not there (<c>aiUsed: false</c> with this <c>reason</c>): the client then does what it did
/// before the AI (a text composed from the data, the field left to the person). Shared by the endpoints that suggest a text to a person who
/// reviews it, so that every client reads the same four reasons.
/// </summary>
public static class AiSuggestionFallbackReasons
{
    public const string AiNotConfigured = "ai_not_configured";

    public const string AiBudgetExhausted = "ai_budget_exhausted";

    public const string AiUnavailable = "ai_unavailable";

    public const string AiInvalidAnswer = "ai_invalid_answer";
}
