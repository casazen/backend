namespace Casazen.Core.Entities.Enums;

/// <summary>Life of a tenant condominium settlement. Stored as an integer: never renumber.</summary>
public enum TenantSettlementStatus
{
    Draft = 0,
    Confirmed = 1,
    Posted = 2,
    Settled = 3,
    Superseded = 4,
}
