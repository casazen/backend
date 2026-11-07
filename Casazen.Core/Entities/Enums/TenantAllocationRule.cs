namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How a consuntivo line is shared onto a lease. Stored as an integer: never renumber.
/// The default is <see cref="DailyProration"/> unless the contract names another rule.
/// </summary>
public enum TenantAllocationRule
{
    FixedContractAmount = 0,
    DailyProration = 1,
    MonthlyProration = 2,
    Consumption = 3,
    ExplicitAmount = 4,
    NotChargeable = 5,

    /// <summary>The contract names a rule this version does not apply. The line is not charged.</summary>
    ContractSpecific = 6,
}
