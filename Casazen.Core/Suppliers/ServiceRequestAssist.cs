using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

public static class ServiceRequestAssistLimits
{
    public const int MinTextLength = 8;

    public const int MaxTextLength = 600;

    public const int MaxSummaryLength = 300;

    public const int MaxExplanationLength = 160;
}

public static class ServiceRequestAssistChannels
{
    public const string CasaZen = "casazen";

    public const string External = "external";
}

public static class ServiceRequestAssistReasons
{
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
    public static ServiceRequestAssist Without(string reason) => new(false, reason, null, null, null, null, null, 0);
}
