namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a tenant settlement goes. Stored as an integer: never renumber.
/// The default is <see cref="NextInstallment"/>. The choice is stored on the settlement.
/// </summary>
public enum TenantSettlementDisposition
{
    /// <summary>Added as a component of the next rent installment that has not been sent or paid.</summary>
    NextInstallment = 0,

    /// <summary>Its own installment. The rent installments already issued are not changed.</summary>
    SeparateInstallment = 1,

    /// <summary>A credit the landlord refunds outside the rent plan. No installment is changed.</summary>
    Refund = 2,

    /// <summary>Kept as the tenant's prior balance for the next consuntivo. No installment is changed.</summary>
    CarryForward = 3,
}
