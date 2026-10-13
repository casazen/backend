namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How a <see cref="TouristTaxRate"/> turns into an amount per taxable guest per night (task BK-03, RS-7).
/// </summary>
public enum TouristTaxCalculationMethod
{
    /// <summary>Fixed amount per person per night (<see cref="TouristTaxRate.RatePerPersonPerNight"/>).</summary>
    PerPersonPerNight = 0,

    /// <summary>
    /// Percentage of the night price per person (<see cref="TouristTaxRate.PercentOfNightlyPrice"/>), capped at
    /// <see cref="TouristTaxRate.CapPerPersonPerNight"/> when set (Bologna in the RS-7 research: 10,5% max 7,00 €).
    /// </summary>
    PercentOfNightlyPrice = 1,
}
