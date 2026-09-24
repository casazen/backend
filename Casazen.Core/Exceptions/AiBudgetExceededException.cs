namespace Casazen.Core.Exceptions;

/// <summary>
/// The monthly platform AI budget (<see cref="Services.IAiBudgetGuard"/>) cannot cover the call: the provider was
/// not called. HTTP 422 with code <c>ai_budget_exhausted</c> when it reaches the API; batch jobs stop at the first one.
/// </summary>
public sealed class AiBudgetExceededException()
    : DomainRuleException(ErrorCode, ErrorMessageKey)
{
    /// <summary>Stable API error code.</summary>
    public const string ErrorCode = "ai_budget_exhausted";

    /// <summary>Resource key of the user-facing message (<c>SharedResources.resx</c>).</summary>
    public const string ErrorMessageKey = "AiBudgetExhaustedDetail";
}
