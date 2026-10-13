namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How a territorial agreement combines the percentage coefficients on the rent per square metre (furniture, term,
/// air conditioning). The MB agreement says only that they "are cumulative" (<c>canone_concordato.md</c>, class D):
/// <see cref="Additive"/> (1 + sum) is the documented reading, kept configurable per agreement until a signatory
/// organization confirms it.
/// </summary>
public enum CoefficientCombination
{
    /// <summary>factor = 1 + Σ percentages.</summary>
    Additive = 0,

    /// <summary>factor = Π (1 + percentage).</summary>
    Multiplicative = 1,
}
