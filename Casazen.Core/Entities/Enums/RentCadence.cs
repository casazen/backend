namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How often the rent is due (LT-06, #269): one installment every <see cref="RentInstallmentPlan.MonthsPer"/> months
/// from the lease start. Stored as an integer: never renumber.
/// </summary>
public enum RentCadence
{
    Monthly = 0,
    Bimonthly = 1,
    Quarterly = 2,
    Semiannual = 3,
}
