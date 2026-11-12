namespace Casazen.Core.Entities.Enums;

/// <summary>Where a recoverability choice came from. Stored as an integer: never renumber.</summary>
public enum ExpenseClassificationSource
{
    Document = 0,
    Contract = 1,
    Rule = 2,
    Manual = 3,
}
