namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What one part of a rent installment is (CondoLedger phase 1). Stored as an integer: never renumber.
/// <see cref="Entities.RentLedgerEntry.AmountDue"/> stays the total of the installment and equals the sum of its components.
/// </summary>
public enum RentLedgerComponentKind
{
    /// <summary>The rent itself.</summary>
    Rent = 0,

    /// <summary>The condominium advance (the contractual one, or later the one taken from a budget).</summary>
    CondominiumAdvance = 1,

    /// <summary>The condominium settlement (debit or credit) posted on its own or on an installment.</summary>
    CondominiumSettlement = 2,

    /// <summary>Any other charge that is part of the amount due.</summary>
    Other = 3,
}
