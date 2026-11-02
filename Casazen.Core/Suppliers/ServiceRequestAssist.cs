using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>Limits of «Descrivi di cosa hai bisogno» in the guided request (WIZ-AI-REQUEST).</summary>
public static class ServiceRequestAssistLimits
{
    /// <summary>The shortest description worth asking the AI about.</summary>
    public const int MinTextLength = 8;

    /// <summary>The longest description the AI reads (the host's own words, scrubbed, are all that goes to the provider).</summary>
    public const int MaxTextLength = 600;

    /// <summary>The summary the AI writes for the supplier: it becomes the notes of the request, which the host reads and changes.</summary>
    public const int MaxSummaryLength = 300;

    /// <summary>The sentence that tells the host why the AI chose what it chose.</summary>
    public const int MaxExplanationLength = 160;
}

/// <summary>Who the request goes to, as the AI proposes it. The rule is not the AI's: CasaZen suppliers come first whenever one covers the property.</summary>
public static class ServiceRequestAssistChannels
{
    /// <summary>At least one active CasaZen supplier covers the comune of the property for the category.</summary>
    public const string CasaZen = "casazen";

    /// <summary>No CasaZen supplier does (or the property has no comune yet): the partners are proposed instead.</summary>
    public const string External = "external";
}

/// <summary>
/// Why there is no suggestion when <see cref="ServiceRequestAssist.AiUsed"/> is false: the four reasons every AI suggestion shares
/// (<see cref="Casazen.Core.Services.AiSuggestionFallbackReasons"/>) and the one of this endpoint.
/// </summary>
public static class ServiceRequestAssistReasons
{
    /// <summary>The model answered, and said it could not tell what the work is: the host chooses the service as before.</summary>
    public const string NotUnderstood = "ai_not_understood";
}

/// <summary>
/// What the AI made of the host's description of a job: the kind of work, how urgent it is, a sentence for the supplier and who to ask. Nothing
/// is sent anywhere: the wizard fills in its answers with it and the host reads them, changes them and sends the request.
/// </summary>
/// <param name="AiUsed">True when the AI worked out the kind of work. False: there is nothing to suggest and <paramref name="Reason"/> says why.</param>
/// <param name="Reason">Why the AI is not behind a suggestion: <c>ai_not_configured</c>, <c>ai_budget_exhausted</c>, <c>ai_unavailable</c>, <c>ai_invalid_answer</c> or <c>ai_not_understood</c>.</param>
/// <param name="Category">A code of <see cref="ServiceCategories.All"/>.</param>
/// <param name="Urgency">How soon the work is needed; the host can change it.</param>
/// <param name="Summary">The work in a few words for the supplier (no names, phones, e-mails or addresses).</param>
/// <param name="Explanation">One sentence for the host: why this category and this urgency.</param>
/// <param name="Channel"><see cref="ServiceRequestAssistChannels"/>: decided by the rule above, from the suppliers around the property.</param>
/// <param name="CasazenSuppliers">How many CasaZen suppliers cover the comune of the property for the category.</param>
public sealed record ServiceRequestAssist(
    bool AiUsed,
    string? Reason,
    string? Category,
    ServiceRequestUrgency? Urgency,
    string? Summary,
    string? Explanation,
    string? Channel,
    int CasazenSuppliers)
{
    /// <summary>No suggestion, and why.</summary>
    public static ServiceRequestAssist Without(string reason) => new(false, reason, null, null, null, null, null, 0);
}
