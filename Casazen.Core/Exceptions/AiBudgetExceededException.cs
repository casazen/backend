namespace Casazen.Core.Exceptions;

public sealed class AiBudgetExceededException()
    : DomainRuleException(ErrorCode, ErrorMessageKey)
{
    public const string ErrorCode = "ai_budget_exhausted";

    public const string ErrorMessageKey = "AiBudgetExhaustedDetail";
}
