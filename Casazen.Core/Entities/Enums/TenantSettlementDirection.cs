namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Sign of a tenant condominium settlement. Stored as an integer: never renumber.
/// Positive is a debit, negative a credit, and under one cent is <see cref="Balanced"/>.
/// </summary>
public enum TenantSettlementDirection
{
    Debit = 0,
    Credit = 1,
    Balanced = 2,
}
