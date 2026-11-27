namespace Casazen.Core.Entities.Enums;

/// <summary>Whether a person has accepted the classification. Stored as an integer: never renumber.</summary>
public enum ExpenseReviewStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}
