namespace Casazen.Core.Services;

/// <summary>
/// Why a text the AI should have suggested is not there (<c>aiUsed: false</c> with this <c>reason</c>): the client then does what it did
/// before the AI (a text composed from the data, the field left to the person). Shared by the endpoints that suggest a text to a person who
/// reviews it, so that every client reads the same four reasons.
/// </summary>
public static class AiSuggestionFallbackReasons
{
    /// <summary>No external AI provider is active (<c>Ai:Provider</c> is the stub, or there is no key).</summary>
    public const string AiNotConfigured = "ai_not_configured";

    /// <summary>The platform's monthly AI budget cannot cover the call.</summary>
    public const string AiBudgetExhausted = "ai_budget_exhausted";

    /// <summary>The provider did not answer (error, timeout).</summary>
    public const string AiUnavailable = "ai_unavailable";

    /// <summary>The provider answered with something that is not what the prompt asked for.</summary>
    public const string AiInvalidAnswer = "ai_invalid_answer";
}
