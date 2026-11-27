namespace Casazen.Core.Entities.Enums;

/// <summary>Life of one allocation of a consuntivo line onto a lease. Stored as an integer: never renumber.</summary>
public enum TenantAllocationStatus
{
    Proposed = 0,
    Confirmed = 1,
    Excluded = 2,
    Superseded = 3,
}
