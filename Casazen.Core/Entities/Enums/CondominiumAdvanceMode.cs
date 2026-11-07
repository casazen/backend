namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the condominium advance of a lease is decided (CondoLedger phase 1). Stored as an integer: never renumber.
/// A confirmed <see cref="FixedContractAmount"/> is the advance that is charged; a budget never rewrites it.
/// </summary>
public enum CondominiumAdvanceMode
{
    /// <summary>The contract states the periodic advance. It wins over a budget.</summary>
    FixedContractAmount = 0,

    /// <summary>The advance comes from the administrator's budget, split in cents across the installments.</summary>
    BudgetAllocation = 1,

    /// <summary>The contract charges no condominium advance.</summary>
    None = 2,

    /// <summary>The clause was read but a person still has to decide. Nothing is charged.</summary>
    NeedsReview = 3,
}
