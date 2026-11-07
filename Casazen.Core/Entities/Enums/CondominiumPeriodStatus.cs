namespace Casazen.Core.Entities.Enums;

/// <summary>Where a condominium fiscal year stands. Stored as an integer: never renumber.</summary>
public enum CondominiumPeriodStatus
{
    Draft = 0,
    BudgetApproved = 1,
    FinalStatementReceived = 2,
    Closed = 3,
    Reopened = 4,
}
